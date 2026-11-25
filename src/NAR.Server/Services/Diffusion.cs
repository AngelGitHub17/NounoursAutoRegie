using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NAR.Server.Services;

/// <summary>Réglages de la diffusion vers l'extérieur (site Nounours.TV, lecteurs IPTV hors de la maison).</summary>
public sealed class DiffusionSettings
{
    /// <summary>Adresse publique du port de diffusion, vue d'Internet : « 82.67.240.20:8091 » ou « http://tv.mondomaine.fr:8091 ».</summary>
    public string PublicAddress { get; set; } = "";

    /// <summary>Adresse du site Nounours.TV où est déposé nar.php.</summary>
    public string SiteUrl { get; set; } = "https://ntvbydidikaria.fr";
}

/// <summary>
/// Diffusion publique : clé exigée sur le port ouvert sur Internet, adresses à recopier dans
/// Nounours.TV, et fabrication du relais nar.php du site (même principe que son stream.php).
/// </summary>
public sealed class Diffusion
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly NarOptions _options;
    private readonly string _keyPath, _settingsPath;
    private readonly object _lock = new();
    private string _key;
    private DiffusionSettings _settings;

    public Diffusion(IOptions<NarOptions> options)
    {
        _options = options.Value;
        var dir = _options.ResolvedDataDirectory;
        _keyPath = Path.Combine(dir, "diffusion.key");
        _settingsPath = Path.Combine(dir, "diffusion.json");
        _key = File.Exists(_keyPath) ? File.ReadAllText(_keyPath).Trim() : "";
        if (_key.Length < 16) _key = NewKey();
        try
        {
            _settings = File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<DiffusionSettings>(File.ReadAllText(_settingsPath)) ?? new()
                : new();
        }
        catch (JsonException) { _settings = new(); }
    }

    public int PublicPort => _options.PublicPort;
    public int LocalPort => _options.HttpPort;
    public string Key { get { lock (_lock) return _key; } }
    public DiffusionSettings Settings { get { lock (_lock) return new DiffusionSettings { PublicAddress = _settings.PublicAddress, SiteUrl = _settings.SiteUrl }; } }

    public bool KeyMatches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var expected = Encoding.UTF8.GetBytes(Key);
        var given = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    public string RegenerateKey()
    {
        lock (_lock) return _key = NewKey();
    }

    public void Save(DiffusionSettings settings)
    {
        lock (_lock)
        {
            _settings = new DiffusionSettings
            {
                PublicAddress = settings.PublicAddress.Trim().TrimEnd('/'),
                SiteUrl = settings.SiteUrl.Trim().TrimEnd('/'),
            };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, Json));
        }
    }

    /// <summary>« http://hôte:port » d'après l'adresse publique saisie (vide si non renseignée).</summary>
    public string PublicBase
    {
        get
        {
            var a = Settings.PublicAddress;
            if (string.IsNullOrWhiteSpace(a)) return "";
            var raw = a.Contains("://") ? a : "http://" + a;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return "";
            var authority = raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
            return authority.Contains(':') ? $"{uri.Scheme}://{uri.Authority}" : $"{uri.Scheme}://{uri.Host}:{PublicPort}";
        }
    }

    public string PublicEpgUrl(int channelId) => $"{PublicBase}/live/epg.xml?chaine={channelId}&cle={Uri.EscapeDataString(Key)}";
    public string PublicM3uUrl => $"{PublicBase}/live/chaines.m3u?cle={Uri.EscapeDataString(Key)}";
    public string SiteStreamUrl(int channelId) => $"{Settings.SiteUrl}/nar.php?c={channelId}";

    private string NewKey()
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        File.WriteAllText(_keyPath, key);
        return key;
    }

    /// <summary>Le relais à déposer dans le site Nounours.TV (www3/nar.php), déjà réglé.</summary>
    public string NarPhp()
    {
        var template = NarPhpTemplate;
        return template
            .Replace("{{BASE}}", PhpString(PublicBase))
            .Replace("{{KEY}}", PhpString(Key));
    }

    private static string PhpString(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private const string NarPhpTemplate = """
<?php
/**
 * nar.php — relais HLS pour les chaînes NounoursAutoRegie (même principe que stream.php
 * pour ErsatzTV) : le site est en HTTPS, la régie répond en HTTP chez toi, donc les
 * playlists et les segments repassent par ici. Les règles du site s'appliquent : palier
 * d'abonnement, chaînes adultes, chaîne active.
 *
 * Installation : déposer ce fichier dans www3/ (à côté de stream.php), puis dans
 * Administration → Chaînes IPTV, créer une chaîne « Flux externe » avec l'adresse
 *   https://<ton site>/nar.php?c=<numéro de la chaîne dans la régie>
 *
 * Fabriqué par NounoursAutoRegie (page Diffusion). Si tu changes la clé ou l'adresse
 * publique dans la régie, télécharge-le à nouveau.
 */

require_once __DIR__ . '/config.php';

const NAR_BASE = {{BASE}};
const NAR_KEY  = {{KEY}};

header('Access-Control-Allow-Origin: *');
header('Cache-Control: no-cache');

function nar_fetch(string $url, int $timeout = 15): array {
    $ch = curl_init($url);
    curl_setopt_array($ch, [
        CURLOPT_RETURNTRANSFER => true, CURLOPT_TIMEOUT => $timeout, CURLOPT_CONNECTTIMEOUT => 5,
        CURLOPT_FOLLOWLOCATION => false,
    ]);
    $body = curl_exec($ch);
    $code = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    $err = curl_error($ch);
    curl_close($ch);
    return ['body' => $body, 'code' => $code, 'err' => $err];
}

function nar_fail(int $code, string $message): never {
    http_response_code($code);
    header('Content-Type: text/plain; charset=utf-8');
    exit($message);
}

function nar_detail(array $r): string {
    if ($r['err']) return "régie injoignable ({$r['err']}) — vérifie que la redirection de port de la box vers le conteneur est faite et que l'adresse publique est juste dans la page Diffusion de la régie";
    if ($r['code'] == 403) return "la régie refuse la clé — télécharge à nouveau nar.php depuis la page Diffusion de la régie";
    if ($r['code'] == 404) return "chaîne inconnue ou éteinte dans la régie";
    return "la régie a répondu avec le code {$r['code']}";
}

if (NAR_BASE === '') nar_fail(500, "adresse publique de la régie non renseignée : remplis-la dans la page Diffusion de la régie puis télécharge à nouveau nar.php");

$c = (int)($_GET['c'] ?? 0);
$seg = (string)($_GET['seg'] ?? '');
if ($c <= 0 || $c > 9999) nar_fail(400, 'chaîne invalide');

// La chaîne du site dont l'adresse de flux pointe ici : ce sont ses règles qui s'appliquent.
$channel = null;
foreach (channels_all() as $ch) {
    if (preg_match('#nar\.php\?(?:[^"]*&)?c=' . $c . '(?:&|$)#', (string)($ch['stream_url'] ?? ''))) { $channel = $ch; break; }
}
if (!$channel || empty($channel['active'])) nar_fail(404, "aucune chaîne active du site n'utilise nar.php?c=$c (Administration → Chaînes IPTV)");

$token = (string)($_GET['token'] ?? '');
$tokenSuffix = $token !== '' ? '&token=' . urlencode($token) : '';
if ($token === '' && !empty($_GET['_na_token']) && preg_match('/^[A-Za-z0-9._-]{32,200}$/', (string)$_GET['_na_token'])) {
    $tokenSuffix = '&_na_token=' . urlencode((string)$_GET['_na_token']);
}
if ($token !== '') {
    $auth = m3u_token_verify($token);
    if (!$auth) nar_fail(403, 'lien invalide ou révoqué');
    $level = $auth['level']; $isAdmin = false; $is18 = false;
} else {
    $v = viewer();
    $level = $v['level']; $isAdmin = $v['admin']; $is18 = $v['is18'];
}
if (!channel_is_unlocked($channel, $level, $isAdmin)) nar_fail(403, 'abonnement requis pour cette chaîne');
if (!empty($channel['is_adult']) && !$is18 && !$isAdmin) nar_fail(403, 'contenu réservé aux adultes');

$key = '?cle=' . rawurlencode(NAR_KEY);

// ── Segment vidéo ────────────────────────────────────────────────
if ($seg !== '') {
    if (!preg_match('/^seg_\d+\.ts$/', $seg)) nar_fail(400, 'segment invalide');
    $r = nar_fetch(NAR_BASE . '/live/' . $c . '/' . $seg . $key, 20);
    if ($r['err'] || $r['code'] != 200) nar_fail(502, 'segment indisponible : ' . nar_detail($r));
    header('Content-Type: video/MP2T');
    header('Cache-Control: public, max-age=60');
    header('Content-Length: ' . strlen($r['body']));
    echo $r['body'];
    exit;
}

// ── Playlist ─────────────────────────────────────────────────────
$r = nar_fetch(NAR_BASE . '/live/' . $c . '/index.m3u8' . $key, 10);
if ($r['err'] || $r['code'] != 200 || !$r['body']) nar_fail(502, 'playlist indisponible : ' . nar_detail($r));
$out = [];
foreach (explode("\n", trim($r['body'])) as $line) {
    $line = rtrim($line);
    if ($line === '' || $line[0] === '#') { $out[] = $line; continue; }
    $name = basename(parse_url($line, PHP_URL_PATH) ?: '');
    $out[] = $name !== '' ? 'nar.php?c=' . $c . '&seg=' . rawurlencode($name) . $tokenSuffix : $line;
}
$txt = implode("\n", $out) . "\n";
header('Content-Type: application/vnd.apple.mpegurl');
header('Content-Length: ' . strlen($txt));
echo $txt;

""";
}
