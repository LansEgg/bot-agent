using System.Security.Cryptography;

namespace BotAgent.Adapters.Persistence;

internal sealed class PanelPasswordStore
{
    private const string InitialPasswordEnvironmentVariable = "QQCHAT_PANEL_PASSWORD";
    private const int Iterations = 210_000;
    private readonly SecretsStore _secrets = new();
    private readonly object _gate = new();
    private byte[] _salt;
    private byte[] _hash;

    public bool MustChange { get; private set; }
    public bool IsConfigured { get; private set; }
    public bool Created { get; }

    public PanelPasswordStore(string path)
    {
        var authDisabled = string.Equals(Environment.GetEnvironmentVariable("QQCHAT_DISABLE_PANEL_AUTH"), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("QQCHAT_DISABLE_PANEL_AUTH"), "1")
            || string.Equals(Environment.GetEnvironmentVariable("QQCHAT_PANEL_AUTH"), "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("QQCHAT_PANEL_AUTH"), "0");

        var initialPassword = Environment.GetEnvironmentVariable(InitialPasswordEnvironmentVariable);
        if (string.Equals(initialPassword, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(initialPassword, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            authDisabled = true;
        }

        if (authDisabled)
        {
            _salt = Array.Empty<byte>();
            _hash = Array.Empty<byte>();
            MustChange = false;
            IsConfigured = false;
            return;
        }

        var stored = _secrets.Load("panelPassword", throwOnError: true);
        var legacy = SecretFiles.TryRead(path, out var readError);
        if (readError is not null)
            throw new IOException("Cannot read panel password file: " + readError);
        if (stored is not null || legacy is not null)
        {
            var parts = (stored ?? legacy!).Split(':');
            if (parts.Length != 3 || (parts[0] != "0" && parts[0] != "1"))
                throw new InvalidDataException("Invalid panel password file");
            MustChange = parts[0] == "0";
            _salt = Convert.FromBase64String(parts[1]);
            _hash = Convert.FromBase64String(parts[2]);
            if (_salt.Length != 16 || _hash.Length != 32)
                throw new InvalidDataException("Invalid panel password hash");
            if (stored is null)
                _secrets.Save("panelPassword", legacy, throwOnError: true);
            if (legacy is not null)
                File.Delete(path);
            IsConfigured = true;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(initialPassword) && initialPassword.Length >= 6 && initialPassword.Length <= 200)
            {
                _salt = RandomNumberGenerator.GetBytes(16);
                _hash = Hash(initialPassword, _salt);
                MustChange = initialPassword.Length < 10;
                IsConfigured = true;
                _secrets.Save("panelPassword", Serialize(), throwOnError: true);
            }
            else
            {
                _salt = Array.Empty<byte>();
                _hash = Array.Empty<byte>();
                MustChange = false;
                IsConfigured = false;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _secrets.Save("panelPassword", null, throwOnError: false);
            _salt = Array.Empty<byte>();
            _hash = Array.Empty<byte>();
            MustChange = false;
            IsConfigured = false;
        }
    }

    public bool Verify(string password)
    {
        if (!IsConfigured) return true;
        if (password.Length > 200 || _salt.Length == 0) return false;
        lock (_gate)
            return CryptographicOperations.FixedTimeEquals(Hash(password, _salt), _hash);
    }

    public void Change(string password)
    {
        lock (_gate)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Hash(password, salt);
            var content = $"1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
            _secrets.Save("panelPassword", content, throwOnError: true);
            _salt = salt;
            _hash = hash;
            MustChange = false;
        }
    }

    private string Serialize() => $"{(MustChange ? 0 : 1)}:{Convert.ToBase64String(_salt)}:{Convert.ToBase64String(_hash)}";

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

}
