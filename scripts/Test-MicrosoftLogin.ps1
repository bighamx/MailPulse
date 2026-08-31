param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
# Synthetic accounts only: no saved config, real tokens or network calls.
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with powershell.exe -STA' }
$repoPath = Split-Path $PSScriptRoot -Parent
$exePath = Join-Path $repoPath ("bin\{0}\net48\MailPulse.exe" -f $Configuration)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Security
[void][Reflection.Assembly]::LoadFrom($exePath)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$script:passed = 0
function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:passed++
}
function Field($dialog, [string]$name) { $dialog.GetType().GetField($name, $flags).GetValue($dialog) }
function SetField($dialog, [string]$name, $value) { $dialog.GetType().GetField($name, $flags).SetValue($dialog, $value) }
function RequestedId($dialog) { $dialog.GetType().GetMethod('GetRequestedOAuthClientId', $flags).Invoke($dialog, $null) }
function Account([string]$clientId, [string]$graphToken) {
    $account = New-Object MailPulse.Models.AccountConfig
    $account.User = 'synthetic@live.cn'
    $account.Host = 'outlook.office365.com'
    $account.UseOAuth = $true
    $account.OAuthClientId = $clientId
    $account.EncryptedGraphRefreshToken = $graphToken
    return $account
}
$defaultId = [MailPulse.Services.MicrosoftOAuthService]::DefaultClientId
$customId = '11111111-2222-4333-8444-555555555555'
Assert ($defaultId -eq '7c03e9d6-9a11-418a-afaa-c959a3154bdd') 'Default client ID mismatch'
$delayMethod = [MailPulse.Services.MailMonitorService].GetMethod('GetSuccessfulLoopDelaySeconds', [Reflection.BindingFlags]'Static,NonPublic')
[MailPulse.Models.AccountConfig]$graphTiming = Account $defaultId 'opaque-graph-token'
Assert ($delayMethod.Invoke($null, [object[]]@($graphTiming)) -eq 5) 'Graph polling should detect mail in about five seconds'
[MailPulse.Models.AccountConfig]$popTiming = Account '' ''
$popTiming.UseOAuth = $false
$popTiming.Protocol = [MailPulse.Models.MailProtocol]::Pop3
$popTiming.PollIntervalSeconds = 45
Assert ($delayMethod.Invoke($null, [object[]]@($popTiming)) -eq 45) 'POP3 should honor configured interval'
$popTiming.PollIntervalSeconds = 3
Assert ($delayMethod.Invoke($null, [object[]]@($popTiming)) -eq 15) 'POP3 should retain safe minimum interval'
$popTiming.Protocol = [MailPulse.Models.MailProtocol]::Imap
Assert ($delayMethod.Invoke($null, [object[]]@($popTiming)) -eq 2) 'IMAP reconnect should not add a long delay'
foreach ($theme in @('Light', 'Dark')) {
    [MailPulse.UI.Theme]::Apply([MailPulse.UI.Theme]::ParseMode($theme))
    $dialog = New-Object MailPulse.UI.AccountDialog -ArgumentList @($null)
    try {
        $mode = Field $dialog '_cbOAuthMode'
        $input = Field $dialog '_tbOAuthClientId'
        Assert ($mode.SelectedIndex -eq 0) 'New accounts must default to shared Graph login'
        Assert ((RequestedId $dialog) -eq $defaultId) 'Default login must send the shared ID'
        Assert ($input.Visibility -eq 'Collapsed') 'Default login must hide ID input'
        Assert ((Field $dialog '_oauthRegistrationButton').Visibility -eq 'Collapsed') 'Default login must hide registration button'
        Assert (-not $dialog.ResultAccount().UseOAuth) 'Opening dialog must not invent authorization'
        $mode.SelectedIndex = 1
        Assert ($input.Visibility -eq 'Visible') 'Custom login must show ID input'
        Assert ((Field $dialog '_oauthRegistrationButton').Visibility -eq 'Visible') 'Custom login must show registration button'
        foreach ($invalid in @('', '   ', 'not-a-guid')) {
            $input.Text = $invalid
            $rejected = $false
            try { [void](RequestedId $dialog) } catch { $rejected = $true }
            Assert $rejected 'Invalid custom ID must not fall back to legacy login'
        }
        $input.Text = " $customId "
        Assert ((RequestedId $dialog) -eq $customId) 'Custom ID must be trimmed and preserved'
        $mode.SelectedIndex = 2
        Assert ((RequestedId $dialog) -eq '') 'Only explicit legacy mode uses empty ID'
        Assert ($input.Visibility -eq 'Collapsed') 'Legacy mode must hide custom ID'
        $mode.SelectedIndex = 0
        Assert ((RequestedId $dialog) -eq $defaultId) 'Returning to default must ignore custom input'
        # Simulate successful OAuth state, then exercise the real save/encryption and Graph routing.
        SetField $dialog '_authorizedOAuthClientId' (RequestedId $dialog)
        SetField $dialog '_graphRefreshToken' 'synthetic-refresh-token'
        SetField $dialog '_useOAuth' $true
        $saved = $dialog.ResultAccount()
        Assert ($saved.OAuthClientId -eq $defaultId) 'Successful default login must persist explicit ID'
        Assert ([MailPulse.Services.SecureStore]::Unprotect($saved.EncryptedGraphRefreshToken) -eq 'synthetic-refresh-token') 'New token must be DPAPI protected'
        Assert ([MailPulse.Services.MailCenterService]::IsGraphAccount($saved)) 'Default login must route through Graph'
    } finally { $dialog.Close() }
    foreach ($clientId in @($defaultId, $customId)) {
        $existing = Account $clientId 'opaque-graph-token'
        $dialog = New-Object MailPulse.UI.AccountDialog -ArgumentList $existing
        try {
            $expectedMode = if ($clientId -eq $defaultId) { 0 } else { 1 }
            Assert ((Field $dialog '_cbOAuthMode').SelectedIndex -eq $expectedMode) 'Existing app must map to correct mode'
            Assert ((RequestedId $dialog) -eq $clientId) 'Existing app login ID must be preserved'
            (Field $dialog '_cbOAuthMode').SelectedIndex = 0
            $saved = $dialog.ResultAccount()
            Assert ($saved.OAuthClientId -eq $clientId) 'Switching mode without login must preserve authorized client'
            Assert ($saved.EncryptedGraphRefreshToken -eq 'opaque-graph-token') 'Saving must preserve existing token'
        } finally { $dialog.Close() }
    }
    foreach ($legacyField in @('EncryptedRefreshToken', 'EncryptedImapRefreshToken')) {
        $existing = Account '' ''
        $existing.$legacyField = 'opaque-legacy-token'
        $dialog = New-Object MailPulse.UI.AccountDialog -ArgumentList $existing
        try {
            Assert ((Field $dialog '_cbOAuthMode').SelectedIndex -eq 2) 'Existing legacy authorization must remain legacy'
            (Field $dialog '_cbOAuthMode').SelectedIndex = 0
            $saved = $dialog.ResultAccount()
            Assert ($saved.OAuthClientId -eq '') 'Legacy token must not be assigned the default client without login'
            Assert ($saved.EncryptedImapRefreshToken -eq 'opaque-legacy-token') 'Legacy token must survive saving'
            Assert (-not [MailPulse.Services.MailCenterService]::IsGraphAccount($saved)) 'Legacy account must not falsely route to Graph'
        } finally { $dialog.Close() }
    }
}
Write-Output "PASS: $script:passed Microsoft login assertions ($Configuration). No external requests."
