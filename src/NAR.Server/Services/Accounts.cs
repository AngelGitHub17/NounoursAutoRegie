using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using NAR.Server.Data;

namespace NAR.Server.Services;

/// <summary>Comptes locaux : mots de passe hachés (PBKDF2-SHA256, 210 000 itérations) et connexion par cookie.</summary>
public sealed class Accounts(IDbContextFactory<NarDbContext> dbFactory, ILogger<Accounts> logger)
{
    private const int Iterations = 210_000;
    private volatile bool _hasUsers;

    public async Task<bool> HasUsersAsync()
    {
        if (_hasUsers) return true;
        await using var db = await dbFactory.CreateDbContextAsync();
        _hasUsers = await db.Users.AnyAsync();
        return _hasUsers;
    }

    public async Task<AppUser> CreateAsync(string userName, string displayName, string password, string role)
    {
        if (password.Length < 8) throw new ArgumentException("Le mot de passe doit faire au moins 8 caractères.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var name = userName.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.UserName == name)) throw new ArgumentException("Cet identifiant existe déjà.");
        var user = new AppUser
        {
            UserName = name,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName.Trim() : displayName.Trim(),
            PasswordHash = Hash(password),
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _hasUsers = true;
        logger.LogInformation("Compte créé : {User} ({Role})", user.UserName, role);
        return user;
    }

    public async Task<AppUser?> CheckAsync(string userName, string password)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var name = userName.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == name);
        if (user is null || !Verify(password, user.PasswordHash))
        {
            logger.LogWarning("Échec de connexion pour « {User} »", name);
            return null;
        }
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task SignInAsync(HttpContext http, AppUser user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UserName),
            new Claim("display_name", user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
    }

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
