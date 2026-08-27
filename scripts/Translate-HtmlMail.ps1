param(
    [string]$InputPath = "mail.html",
    [string]$OutputPath = "mail.translated.html",
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)
# Real-translation harness: reads an HTML file, translates it with the app's first enabled
# LLM config (DPAPI key), and writes the woven result for manual inspection.
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path $PSScriptRoot -Parent
$exePath = Join-Path $repoPath ("bin\{0}\net48\MailPulse.exe" -f $Configuration)
$jsonPath = Join-Path $env:USERPROFILE '.nuget\packages\newtonsoft.json\13.0.3\lib\net45\Newtonsoft.Json.dll'
Add-Type -Path $jsonPath
[void][Reflection.Assembly]::LoadFrom($exePath)

$config = New-Object MailPulse.Services.ConfigService
$config.Load()
$cfg = [MailPulse.Services.LlmClassifier]::FirstEnabled($config.Current.Llms)
if ($cfg -eq $null) { Write-Error 'No enabled LLM config found. Configure one first.' }

$html = [IO.File]::ReadAllText($InputPath, [Text.Encoding]::UTF8)
$layout = [MailPulse.Services.HtmlMailLayout]::Parse($html)
Write-Host ("Units={0}" -f $layout.TotalUnits)
Write-Host "--- first 12 unit sources ---"
for ($i = 0; $i -lt [Math]::Min(12, $layout.TotalUnits); $i++) {
    Write-Host ("[{0}] {1}" -f $i, $layout.Texts[$i])
}

$translator = New-Object MailPulse.Services.MailTranslationService
$result = $translator.TranslateHtmlAsync($layout, 'Windows 11', $cfg,
    [Threading.CancellationToken]::None).GetAwaiter().GetResult()
[IO.File]::WriteAllText($OutputPath, $result, [Text.Encoding]::UTF8)
Write-Host ("Translated units: {0}/{1}" -f $layout.CompletedUnits, $layout.TotalUnits)
Write-Host "Output: $OutputPath"
Write-Host ("Translated subject: {0}" -f $layout.TranslatedSubject)