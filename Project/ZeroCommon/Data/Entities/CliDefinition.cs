namespace Agent.Common.Data.Entities;

public class CliDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string? Arguments { get; set; }
    public bool IsBuiltIn { get; set; }
    public int SortOrder { get; set; }

    // ── Remote Shell (M0021) ──
    // When IsRemote=true, the terminal launcher appends an `ssh <user>@<host>`
    // command to the shell's startup args (CMD `/K`, PowerShell `-NoExit -Command`).
    // PublicKey mode loads a .pem file via `-i`; Password mode never stores the
    // plaintext — EncryptedPassword holds the base64 DPAPI(CurrentUser) blob and
    // is decrypted only at launch time, then handed to the user via clipboard.
    public bool IsRemote { get; set; }
    public string? SshHost { get; set; }
    public string? SshUser { get; set; }
    public string? SshAuthMethod { get; set; }
    public string? SshKeyPath { get; set; }
    public string? EncryptedPassword { get; set; }
}
