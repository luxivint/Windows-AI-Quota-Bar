// Release signing for the in-app updater.
//
// The app embeds the PUBLIC key (Updates.cs) and only installs an update whose Setup.exe carries a valid
// signature (Setup.exe.sig) made with the matching PRIVATE key. The private key never leaves the maintainer's
// PC: it is stored DPAPI-encrypted for the current Windows user, outside the repository. Someone who could
// replace files on the GitHub release still cannot produce a valid signature.
//
//   keygen                    create the key pair (once) and print the public key for Updates.cs
//   pubkey                    print the public key
//   sign <file>               write <file>.sig (base64 ECDSA P-256 / SHA-256 signature)
//   verify <file>             check <file>.sig against the stored public key
//   export <path>             write a password-protected backup of the private key (asks for a password)
//   import <path>             restore the private key from such a backup
using System.Security.Cryptography;
using System.Text;

const string Entropy = "WindowsAIQuotaBar.ReleaseSigning.v1";
var keyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsAIQuotaBar-release");
var keyFile = Path.Combine(keyDir, "update-signing.key");

ECDsa LoadKey()
{
    if (!File.Exists(keyFile)) throw new InvalidOperationException($"No signing key at {keyFile}. Run: keygen (or import a backup).");
    var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(keyFile), Encoding.UTF8.GetBytes(Entropy), DataProtectionScope.CurrentUser);
    var k = ECDsa.Create();
    k.ImportPkcs8PrivateKey(pkcs8, out _);
    CryptographicOperations.ZeroMemory(pkcs8);
    return k;
}

void SaveKey(ECDsa k)
{
    Directory.CreateDirectory(keyDir);
    var pkcs8 = k.ExportPkcs8PrivateKey();
    File.WriteAllBytes(keyFile, ProtectedData.Protect(pkcs8, Encoding.UTF8.GetBytes(Entropy), DataProtectionScope.CurrentUser));
    CryptographicOperations.ZeroMemory(pkcs8);
}

string Pub(ECDsa k) => Convert.ToBase64String(k.ExportSubjectPublicKeyInfo());

string ReadPassword(string prompt)
{
    Console.Write(prompt);
    var sb = new StringBuilder();
    while (true)
    {
        var c = Console.ReadKey(intercept: true);
        if (c.Key == ConsoleKey.Enter) break;
        if (c.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        sb.Append(c.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

try
{
    switch (args.FirstOrDefault())
    {
        case "keygen":
            if (File.Exists(keyFile)) throw new InvalidOperationException($"A key already exists at {keyFile}. Refusing to overwrite it.");
            using (var k = ECDsa.Create(ECCurve.NamedCurves.nistP256)) { SaveKey(k); Console.WriteLine(Pub(k)); }
            Console.Error.WriteLine($"Private key saved (DPAPI, this Windows user only): {keyFile}");
            Console.Error.WriteLine("Back it up with: export <path>. Without it, installed copies cannot verify new updates.");
            break;
        case "pubkey":
            using (var k = LoadKey()) Console.WriteLine(Pub(k));
            break;
        case "sign":
        {
            var file = args[1];
            using var k = LoadKey();
            var sig = k.SignData(File.ReadAllBytes(file), HashAlgorithmName.SHA256);
            File.WriteAllText(file + ".sig", Convert.ToBase64String(sig) + "\n");
            Console.WriteLine($"Signed {Path.GetFileName(file)} -> {Path.GetFileName(file)}.sig");
            break;
        }
        case "verify":
        {
            var file = args[1];
            using var k = LoadKey();
            var ok = k.VerifyData(File.ReadAllBytes(file), Convert.FromBase64String(File.ReadAllText(file + ".sig").Trim()), HashAlgorithmName.SHA256);
            Console.WriteLine(ok ? "OK: signature valid" : "FAIL: signature invalid");
            return ok ? 0 : 1;
        }
        case "export":
        {
            using var k = LoadKey();
            var pw = ReadPassword("Backup password: ");
            if (pw.Length < 12) throw new InvalidOperationException("Use at least 12 characters.");
            if (ReadPassword("Repeat password: ") != pw) throw new InvalidOperationException("Passwords do not match.");
            var enc = k.ExportEncryptedPkcs8PrivateKey(pw, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
            File.WriteAllText(args[1], PemEncoding.WriteString("ENCRYPTED PRIVATE KEY", enc));
            Console.WriteLine($"Backup written to {args[1]}. Keep it offline (password manager, USB stick).");
            break;
        }
        case "import":
        {
            if (File.Exists(keyFile)) throw new InvalidOperationException($"A key already exists at {keyFile}.");
            using var k = ECDsa.Create();
            k.ImportFromEncryptedPem(File.ReadAllText(args[1]), ReadPassword("Backup password: "));
            SaveKey(k);
            Console.WriteLine("Key imported. Public key: " + Pub(k));
            break;
        }
        default:
            Console.WriteLine("usage: ReleaseSigner keygen | pubkey | sign <file> | verify <file> | export <path> | import <path>");
            return 2;
    }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}
