using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AgentLock.Security;

public readonly record struct VerificationResult(bool Success, TimeSpan RetryAfter, string Message);

/// <summary>Stores a salted password verifier and persistent retry state for the current Windows user.</summary>
public sealed class PasswordStore
{
    private const int Iterations = 600_000;
    private const int SaltLength = 32;
    private const int HashLength = 32;
    private const int MaximumPasswordLength = 256;
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _root;
    private readonly string _path;
    private readonly string _mutexName;
    private readonly object _sync = new();
    private bool _replacementAuthorized;

    public PasswordStore(string? root = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentLock")));
        if (string.Equals(_root, Path.GetPathRoot(_root), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请使用独立的密码记录文件夹。", nameof(root));

        _path = Path.Combine(_root, "credential.json");
        _mutexName = "Local\\AgentLock.PasswordStore." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(_root.ToUpperInvariant())));
    }

    // An invalid record is still configured: it must never silently become a fresh setup.
    public bool IsConfigured => File.Exists(_path);

    public bool CanReplacePassword
    {
        get
        {
            lock (_sync)
                return _replacementAuthorized;
        }
    }

    /// <summary>
    /// Authorizes one replacement after the controller has observed a real Windows lock/unlock cycle.
    /// This method does not authenticate Windows credentials; the controller owns that check and must
    /// never call it merely because an AgentLock password dialog was cancelled or protection ended.
    /// </summary>
    public void AuthorizePasswordResetAfterNativeUnlock()
    {
        lock (_sync)
            _replacementAuthorized = true;
    }

    /// <summary>Call before entering protection so prior authentication cannot authorize a later reset.</summary>
    public void RevokePasswordResetAuthorization()
    {
        lock (_sync)
            _replacementAuthorized = false;
    }

    public void SetPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < 8 || password.Length > MaximumPasswordLength || string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("保护密码须为 8–256 个字符，且不能全部为空格。", nameof(password));

        lock (_sync)
        {
            using var mutex = AcquireMutex();
            if (IsConfigured && !_replacementAuthorized)
                throw new InvalidOperationException("已有保护密码。请先验证原密码，或通过 Windows 原生解锁后恢复。 ");

            EnsurePrivateDirectory();
            var salt = RandomNumberGenerator.GetBytes(SaltLength);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashLength);
            try
            {
                WriteState(new CredentialState
                {
                    Version = 1,
                    Iterations = Iterations,
                    Salt = Convert.ToBase64String(salt),
                    Hash = Convert.ToBase64String(hash),
                    FailedAttempts = 0,
                    RetryNotBeforeUtc = DateTimeOffset.MinValue
                });
                _replacementAuthorized = false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
                CryptographicOperations.ZeroMemory(salt);
            }
        }
    }

    public VerificationResult Verify(string password)
    {
        lock (_sync)
        {
            try
            {
                using var mutex = AcquireMutex();
                if (!IsConfigured)
                    return new(false, TimeSpan.Zero, "尚未设置保护密码。");

                var state = ReadState();
                var now = DateTimeOffset.UtcNow;
                var retryAfter = state.RetryNotBeforeUtc - now;
                if (retryAfter > TimeSpan.Zero)
                {
                    // A clock correction must not turn a short delay into an indefinite lockout.
                    if (retryAfter > MaximumDelay)
                    {
                        retryAfter = MaximumDelay;
                        state.RetryNotBeforeUtc = now + retryAfter;
                        WriteState(state);
                    }
                    return new(false, retryAfter, "尝试次数较多，请稍候再试。");
                }

                var salt = Convert.FromBase64String(state.Salt);
                var expected = Convert.FromBase64String(state.Hash);
                byte[]? candidate = null;
                var matches = false;
                try
                {
                    if (password is not null && password.Length <= MaximumPasswordLength)
                    {
                        candidate = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations,
                            HashAlgorithmName.SHA256, HashLength);
                        matches = CryptographicOperations.FixedTimeEquals(candidate, expected);
                    }
                }
                finally
                {
                    if (candidate is not null)
                        CryptographicOperations.ZeroMemory(candidate);
                    CryptographicOperations.ZeroMemory(expected);
                    CryptographicOperations.ZeroMemory(salt);
                }

                if (matches)
                {
                    state.FailedAttempts = 0;
                    state.RetryNotBeforeUtc = DateTimeOffset.MinValue;
                    WriteState(state); // Storage failures fail closed, even for a matching password.
                    _replacementAuthorized = true;
                    return new(true, TimeSpan.Zero, "验证成功。");
                }

                state.FailedAttempts = Math.Min(state.FailedAttempts + 1, 20);
                var delay = DelayFor(state.FailedAttempts);
                state.RetryNotBeforeUtc = delay == TimeSpan.Zero ? DateTimeOffset.MinValue : now + delay;
                WriteState(state);
                return new(false, delay, delay == TimeSpan.Zero ? "密码不正确，请再试一次。" : "密码不正确，请稍候再试。");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or FormatException or CryptographicException or InvalidOperationException
                or System.Security.SecurityException)
            {
                return new(false, TimeSpan.Zero, "无法安全读取密码记录。请取消，并通过 Windows 原生解锁后恢复。");
            }
        }
    }

    private CredentialState ReadState()
    {
        var info = new FileInfo(_path);
        if (info.Length > 8_192 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("密码记录无效。");

        var state = JsonSerializer.Deserialize<CredentialState>(File.ReadAllBytes(_path), JsonOptions)
            ?? throw new InvalidOperationException("密码记录无效。");
        if (state.Version != 1 || state.Iterations != Iterations || state.FailedAttempts is < 0 or > 20
            || string.IsNullOrEmpty(state.Salt) || string.IsNullOrEmpty(state.Hash)
            || Convert.FromBase64String(state.Salt).Length != SaltLength
            || Convert.FromBase64String(state.Hash).Length != HashLength)
            throw new InvalidOperationException("密码记录无效。");
        return state;
    }

    private void WriteState(CredentialState state)
    {
        EnsurePrivateDirectory();
        if (IsConfigured && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("密码记录无效。");

        var temporaryPath = Path.Combine(_root, ".credential-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4_096, FileOptions.WriteThrough))
            {
                ApplyPrivateFileAcl(temporaryPath);
                JsonSerializer.Serialize(stream, state, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (IsConfigured)
                File.Replace(temporaryPath, _path, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, _path);
            ApplyPrivateFileAcl(_path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void EnsurePrivateDirectory()
    {
        Directory.CreateDirectory(_root);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("密码记录文件夹无效。");
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法确认 Windows 用户。");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_root).SetAccessControl(security);
    }

    private static void ApplyPrivateFileAcl(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法确认 Windows 用户。");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private MutexLease AcquireMutex()
    {
        var mutex = new Mutex(initiallyOwned: false, _mutexName);
        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(3)))
                    throw new InvalidOperationException("密码记录暂时忙碌，请稍后重试。");
            }
            catch (AbandonedMutexException)
            {
                // An abandoned mutex is acquired by this thread; an atomic record can still be read.
            }
            return new MutexLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static TimeSpan DelayFor(int failures) => failures < 3
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds(Math.Min(60, 5 * (1 << Math.Min(failures - 3, 4))));

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private sealed class CredentialState
    {
        public int Version { get; set; }
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
        public int FailedAttempts { get; set; }
        public DateTimeOffset RetryNotBeforeUtc { get; set; }
    }
}
