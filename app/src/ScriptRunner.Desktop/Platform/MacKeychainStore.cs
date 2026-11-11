using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ScriptRunner.App.Services;

namespace ScriptRunner.Desktop.Platform;

/// <summary>
/// PINs and passwords in the macOS Keychain, as generic passwords under the service "ScriptRunner", using the
/// Security framework's SecItem functions. Any failure is reported as "not saved" rather than crashing the app.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychainStore : ISecretStore
{
    private const string Service = "ScriptRunner";
    private const string SecurityPath = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8Encoding = 0x08000100;
    private const int ErrSecSuccess = 0;

    private readonly Lazy<Symbols?> symbols = new(Symbols.TryLoad);

    public bool IsAvailable => symbols.Value is not null;

    public string? Load(string key)
    {
        Symbols? s = symbols.Value;
        if (s is null)
        {
            return null;
        }
        using var query = new CFDictionaryBuilder(s);
        query.Add(s.SecClass, s.SecClassGenericPassword);
        query.AddString(s.SecAttrService, Service);
        query.AddString(s.SecAttrAccount, key);
        query.Add(s.SecReturnData, s.BooleanTrue);
        query.Add(s.SecMatchLimit, s.SecMatchLimitOne);
        IntPtr dictionary = query.Build();
        try
        {
            if (ErrSecSuccess != SecItemCopyMatching(dictionary, out IntPtr data) || IntPtr.Zero == data)
            {
                return null;
            }
            try
            {
                int length = (int)CFDataGetLength(data);
                var bytes = new byte[length];
                Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CFRelease(data);
            }
        }
        finally
        {
            CFRelease(dictionary);
        }
    }

    public bool Save(string key, string secret)
    {
        Symbols? s = symbols.Value;
        if (s is null)
        {
            return false;
        }
        Delete(key);
        using var item = new CFDictionaryBuilder(s);
        item.Add(s.SecClass, s.SecClassGenericPassword);
        item.AddString(s.SecAttrService, Service);
        item.AddString(s.SecAttrAccount, key);
        item.AddData(s.SecValueData, Encoding.UTF8.GetBytes(secret));
        IntPtr dictionary = item.Build();
        try
        {
            return ErrSecSuccess == SecItemAdd(dictionary, IntPtr.Zero);
        }
        finally
        {
            CFRelease(dictionary);
        }
    }

    public void Delete(string key)
    {
        Symbols? s = symbols.Value;
        if (s is null)
        {
            return;
        }
        using var query = new CFDictionaryBuilder(s);
        query.Add(s.SecClass, s.SecClassGenericPassword);
        query.AddString(s.SecAttrService, Service);
        query.AddString(s.SecAttrAccount, key);
        IntPtr dictionary = query.Build();
        try
        {
            SecItemDelete(dictionary);
        }
        finally
        {
            CFRelease(dictionary);
        }
    }

    /// <summary>The framework constants the SecItem functions need, read from the frameworks at run time.</summary>
    private sealed class Symbols
    {
        public IntPtr SecClass, SecClassGenericPassword, SecAttrService, SecAttrAccount, SecValueData;
        public IntPtr SecReturnData, SecMatchLimit, SecMatchLimitOne, BooleanTrue;
        public IntPtr KeyCallbacks, ValueCallbacks;

        public static Symbols? TryLoad()
        {
            try
            {
                IntPtr security = NativeLibrary.Load(SecurityPath);
                IntPtr coreFoundation = NativeLibrary.Load(CoreFoundationPath);
                IntPtr Constant(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
                return new Symbols
                {
                    SecClass = Constant(security, "kSecClass"),
                    SecClassGenericPassword = Constant(security, "kSecClassGenericPassword"),
                    SecAttrService = Constant(security, "kSecAttrService"),
                    SecAttrAccount = Constant(security, "kSecAttrAccount"),
                    SecValueData = Constant(security, "kSecValueData"),
                    SecReturnData = Constant(security, "kSecReturnData"),
                    SecMatchLimit = Constant(security, "kSecMatchLimit"),
                    SecMatchLimitOne = Constant(security, "kSecMatchLimitOne"),
                    BooleanTrue = Constant(coreFoundation, "kCFBooleanTrue"),
                    // These two are structures; CFDictionaryCreate takes their addresses.
                    KeyCallbacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks"),
                    ValueCallbacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks"),
                };
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }
    }

    /// <summary>Builds a CFDictionary; releases the strings and data it created once the dictionary is built.</summary>
    private sealed class CFDictionaryBuilder : IDisposable
    {
        private readonly Symbols symbols;
        private readonly List<IntPtr> keys = new();
        private readonly List<IntPtr> values = new();
        private readonly List<IntPtr> owned = new();

        public CFDictionaryBuilder(Symbols symbols) => this.symbols = symbols;

        public void Add(IntPtr key, IntPtr value)
        {
            keys.Add(key);
            values.Add(value);
        }

        public void AddString(IntPtr key, string value)
        {
            IntPtr text = CFStringCreateWithCString(IntPtr.Zero, value, Utf8Encoding);
            owned.Add(text);
            Add(key, text);
        }

        public void AddData(IntPtr key, byte[] value)
        {
            IntPtr data = CFDataCreate(IntPtr.Zero, value, value.Length);
            Array.Clear(value);
            owned.Add(data);
            Add(key, data);
        }

        public IntPtr Build() => CFDictionaryCreate(IntPtr.Zero, keys.ToArray(), values.ToArray(), keys.Count,
            symbols.KeyCallbacks, symbols.ValueCallbacks);

        public void Dispose()
        {
            foreach (IntPtr item in owned)
            {
                CFRelease(item);
            }
        }
    }

    [DllImport(SecurityPath)]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(SecurityPath)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(SecurityPath)]
    private static extern int SecItemDelete(IntPtr query);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CoreFoundationPath)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count,
        IntPtr keyCallbacks, IntPtr valueCallbacks);

    [DllImport(CoreFoundationPath)]
    private static extern void CFRelease(IntPtr item);
}
