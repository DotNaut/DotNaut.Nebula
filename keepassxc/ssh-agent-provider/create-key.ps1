<#
.SYNOPSIS
    Creates an SSH key and stores it in KeePassXC with SSH Agent integration.

.DESCRIPTION
    1. Prompts for database password (once)
    2. Generates an Ed25519 SSH key pair
    3. Creates a KeePassXC entry (or overwrites with -Force)
    4. Imports the private key as attachment
    5. Imports KeeAgent.settings to enable SSH Agent integration
    6. Removes key files from disk (public key retrievable via ssh-add -L)

.PARAMETER Database
    Path to the KeePassXC .kdbx database file.

.PARAMETER Name
    Entry name and SSH key comment (e.g. "my-server").

.PARAMETER Group
    Optional group path in KeePassXC (e.g. "Infrastructure/SSH").

.PARAMETER User
    Optional username for the entry. Defaults to Name.

.PARAMETER Notes
    Optional notes for the KeePassXC entry.

.PARAMETER Force
    Overwrite existing entry if it exists.

.EXAMPLE
    .\create-key.ps1 -Database "C:\path\to\db.kdbx" -Name "my-server" -Group "Infrastructure/SSH"
.EXAMPLE
    .\create-key.ps1 -Database "C:\path\to\db.kdbx" -Name "my-server" -User "deploy" -Force
#>

param(
    [Parameter(Mandatory)] [string] $Database,
    [Parameter(Mandatory)] [string] $Name,
    [string] $Group,
    [string] $User,
    [string] $Notes,
    [switch] $Force
)

$ErrorActionPreference = "Stop"

$entryPath = if ($Group) { "$Group/$Name" } else { $Name }
$attachmentName = "SSH Private Key"
$keyPath = Join-Path $env:TEMP "ssh-key-temp"
$settingsPath = Join-Path $env:TEMP "KeeAgent.settings"

# Helper: pipe password to keepassxc-cli
function Invoke-KeePassCli {
    param([string[]] $Args_)
    for ($i = 0; $i -lt 3; $i++) {
        $password | keepassxc-cli @Args_ 2>&1
        if ($LASTEXITCODE -eq 0) { return }
        if ($i -lt 2) {
            Write-Host "       Retrying (database may be locked by GUI, try to exit)..."
            Start-Sleep -Seconds 5
        }
    }
    throw "keepassxc-cli failed after 3 attempts: $Args_"
}

# 0. Get database password once
$securePassword = Read-Host "Database password" -AsSecureString
$password = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword))

# 1. Check if entry exists
Write-Host "[1/6] Checking for existing entry..."
$null = $password | keepassxc-cli show $Database $entryPath -q 2>&1
if ($LASTEXITCODE -eq 0) {
    if ($Force) {
        Write-Host "       Entry exists, removing (Force)..."
        Invoke-KeePassCli @("rm", $Database, $entryPath)
    } else {
        throw "Entry '$entryPath' already exists. Use -Force to overwrite."
    }
}

# 2. Generate key
Write-Host "[2/6] Generating Ed25519 key..."
if (Test-Path $keyPath) { Remove-Item $keyPath, "$keyPath.pub" -ErrorAction SilentlyContinue }
ssh-keygen -t ed25519 -C $Name -f $keyPath -N ""
if ($LASTEXITCODE -ne 0) { throw "ssh-keygen failed" }

# 3. Create KeePassXC entry
Write-Host "[3/6] Creating KeePassXC entry: $entryPath"
$userName = if ($User) { $User } else { $Name }
$addArgs = @("add", $Database, $entryPath, "-u", $userName)
if ($Notes) { $addArgs += @("--notes", $Notes) }
Invoke-KeePassCli $addArgs

# 4. Import private key as attachment
Write-Host "[4/6] Importing private key as attachment"
Invoke-KeePassCli @("attachment-import", $Database, $entryPath, $attachmentName, $keyPath)

# 5. Generate and import KeeAgent.settings
Write-Host "[5/6] Configuring SSH Agent integration"
$settings = @"
<?xml version="1.0" encoding="utf-8"?>
<EntrySettings xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
    <AllowUseOfSshKey>true</AllowUseOfSshKey>
    <AddAtDatabaseOpen>true</AddAtDatabaseOpen>
    <RemoveAtDatabaseClose>true</RemoveAtDatabaseClose>
    <UseConfirmConstraintWhenAdding>false</UseConfirmConstraintWhenAdding>
    <UseLifetimeConstraintWhenAdding>false</UseLifetimeConstraintWhenAdding>
    <LifetimeConstraintDuration>600</LifetimeConstraintDuration>
    <Location>
        <SelectedType>attachment</SelectedType>
        <AttachmentName>$attachmentName</AttachmentName>
        <SaveAttachmentToTempFile>false</SaveAttachmentToTempFile>
        <FileName />
    </Location>
</EntrySettings>
"@

$utf8bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($settingsPath, $settings, $utf8bom)
Invoke-KeePassCli @("attachment-import", $Database, $entryPath, "KeeAgent.settings", $settingsPath)

# 6. Cleanup
Write-Host "[6/6] Removing key files from disk"
Remove-Item $keyPath, "$keyPath.pub", $settingsPath -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Done. Key stored in KeePassXC entry '$entryPath'."
Write-Host "Lock/unlock the database, then verify: ssh-add -l"
Write-Host "To export public key later: ssh-add -L"
