using System.Net;
using System.Net.Sockets;
using SMBLibrary;
using SMBLibrary.Client;
using SmbFileAttributes = SMBLibrary.FileAttributes;

namespace NAR.Server.Services.Network;

/// <summary>Accès à un partage Windows : identifiants et emplacement.</summary>
public sealed record SmbTarget(string Host, string Share, string User, string Password, string? Domain = null);

public sealed record SmbEntry(string RelativePath, string Name, long Size, DateTime LastWriteUtc);

public sealed class SmbException(string message) : Exception(message);

/// <summary>
/// Session SMB2/3 vers un PC Windows (bibliothèque SMBLibrary, entièrement en .NET) :
/// pas besoin de monter le partage, ce qu'un conteneur LXC non privilégié ne peut pas faire.
/// Une session n'est pas partagée entre plusieurs fils.
/// </summary>
public sealed class SmbSession : IDisposable
{
    private readonly SMB2Client _client = new();
    private ISMBFileStore? _store;

    private SmbSession() { }

    public int MaxReadSize => (int)Math.Min(_client.MaxReadSize, 1024 * 1024);

    /// <summary>Se connecte au PC et s'authentifie (sans ouvrir de partage).</summary>
    public static SmbSession Login(string host, string user, string password, string? domain)
    {
        var session = new SmbSession();
        try
        {
            var address = Resolve(host);
            if (!session._client.Connect(address, SMBTransportType.DirectTCPTransport))
                throw new SmbException($"{host} ne répond pas sur le port 445 (partage de fichiers Windows).");

            var status = session._client.Login(domain ?? "", user, password);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new SmbException(status switch
                {
                    NTStatus.STATUS_LOGON_FAILURE => "Identifiant ou mot de passe refusé par le PC.",
                    NTStatus.STATUS_ACCOUNT_RESTRICTION => "Ce compte n'a pas le droit de se connecter par le réseau (compte sans mot de passe ?).",
                    NTStatus.STATUS_ACCOUNT_DISABLED => "Ce compte Windows est désactivé.",
                    _ => $"Connexion refusée par le PC ({status}).",
                });
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public static SmbSession Open(SmbTarget target)
    {
        var session = Login(target.Host, target.User, target.Password, target.Domain);
        try
        {
            session._store = session._client.TreeConnect(target.Share, out var status);
            if (status != NTStatus.STATUS_SUCCESS || session._store is null)
                throw new SmbException(status == NTStatus.STATUS_BAD_NETWORK_NAME
                    ? $"Le partage « {target.Share} » n'existe pas sur {target.Host}."
                    : $"Accès au partage « {target.Share} » refusé ({status}).");
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Partages visibles (les partages d'administration en $ sont masqués).</summary>
    public List<string> ListShares()
    {
        var shares = _client.ListShares(out var status);
        if (status != NTStatus.STATUS_SUCCESS || shares is null)
            throw new SmbException($"Impossible de lister les partages ({status}).");
        return shares.Where(s => !s.EndsWith('$')).OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Liste les fichiers d'un dossier du partage (chemin relatif, « / » ou « \ »).</summary>
    public IEnumerable<SmbEntry> EnumerateFiles(string relativeDirectory, bool recursive, Func<string, bool> accept)
    {
        var pending = new Stack<string>();
        pending.Push(Normalize(relativeDirectory));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var entry in ListDirectory(dir))
            {
                if (entry.FileName is "." or "..") continue;
                if ((entry.FileAttributes & (SmbFileAttributes.Hidden | SmbFileAttributes.System)) != 0) continue;
                var rel = dir.Length == 0 ? entry.FileName : $"{dir}\\{entry.FileName}";
                if ((entry.FileAttributes & SmbFileAttributes.Directory) != 0)
                {
                    if (recursive) pending.Push(rel);
                }
                else if (accept(entry.FileName))
                {
                    yield return new SmbEntry(rel.Replace('\\', '/'), entry.FileName, entry.EndOfFile, entry.LastWriteTime.ToUniversalTime());
                }
            }
        }
    }

    private List<FileDirectoryInformation> ListDirectory(string dir)
    {
        var store = _store ?? throw new InvalidOperationException("Aucun partage ouvert");
        var status = store.CreateFile(out var handle, out _, dir, AccessMask.GENERIC_READ, SmbFileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new SmbException(status == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND || status == NTStatus.STATUS_OBJECT_PATH_NOT_FOUND
                ? $"Dossier « {dir} » introuvable sur le partage."
                : $"Lecture du dossier « {dir} » impossible ({status}).");
        try
        {
            status = store.QueryDirectory(out var list, handle, "*", FileInformationClass.FileDirectoryInformation);
            if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_NO_MORE_FILES)
                throw new SmbException($"Lecture du dossier « {dir} » impossible ({status}).");
            return list?.OfType<FileDirectoryInformation>().ToList() ?? new();
        }
        finally
        {
            store.CloseFile(handle);
        }
    }

    /// <summary>Ouvre un fichier en lecture et donne sa taille.</summary>
    public (object Handle, long Size) OpenFile(string relativePath)
    {
        var store = _store ?? throw new InvalidOperationException("Aucun partage ouvert");
        var status = store.CreateFile(out var handle, out _, Normalize(relativePath), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            SmbFileAttributes.Normal, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new SmbException(status == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND ? "Fichier introuvable sur le partage." : $"Ouverture impossible ({status}).");

        status = store.GetFileInformation(out var info, handle, FileInformationClass.FileStandardInformation);
        if (status != NTStatus.STATUS_SUCCESS || info is not FileStandardInformation standard)
        {
            store.CloseFile(handle);
            throw new SmbException($"Taille du fichier inconnue ({status}).");
        }
        return (handle, standard.EndOfFile);
    }

    public byte[] Read(object handle, long offset, int count)
    {
        var store = _store ?? throw new InvalidOperationException("Aucun partage ouvert");
        var status = store.ReadFile(out var data, handle, offset, count);
        if (status == NTStatus.STATUS_END_OF_FILE) return [];
        if (status != NTStatus.STATUS_SUCCESS) throw new SmbException($"Lecture interrompue ({status}).");
        return data ?? [];
    }

    public void Close(object handle) => _store?.CloseFile(handle);

    private static string Normalize(string path) => path.Replace('/', '\\').Trim('\\');

    private static IPAddress Resolve(string host)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var addresses = Dns.GetHostAddresses(host);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new SmbException($"Nom de PC inconnu : {host}");
    }

    public void Dispose()
    {
        try { _store?.Disconnect(); } catch { }
        try { _client.Logoff(); } catch { }
        try { _client.Disconnect(); } catch { }
    }
}
