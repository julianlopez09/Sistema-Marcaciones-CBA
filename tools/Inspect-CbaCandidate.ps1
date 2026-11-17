<#
.SYNOPSIS
Inspecciona contratos de entrada y fuentes concretas sin arrancar ni compilar CBA.
.DESCRIPTION
El resultado es una observación estática, no una certificación funcional.
No guarda contenido de documentos ni datos personales en el informe.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProjectRoot,
    [Parameter(Mandatory=$true)][string]$DirectricesPath,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
$document = (Resolve-Path -LiteralPath $DirectricesPath).Path
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'OUTPUT_ALREADY_EXISTS' }
# Never overwrite or create output inside the application under review.
$separator = [IO.Path]::DirectorySeparatorChar
$prefix = $root.TrimEnd($separator) + $separator
if ($output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OUTPUT_MUST_BE_OUTSIDE_REVIEWED_PROJECT'
}
$expectedDocumentHash = '9B7B3605E9CB76D6E1F2F7D142E386E33D01BD61D3DA9AF1FB7D30CE248B307A'
$documentHash = (Get-FileHash -LiteralPath $document -Algorithm SHA256).Hash
if ($documentHash -cne $expectedDocumentHash) { throw 'DIFFERENT_DIRECTRICES_DOCUMENT_REQUIRES_REVIEW' }
$stage = 'MIGRACION/EVIDENCIAS/F13_SR3_AUTONOMOUS_EXPORT_FIX_RESOURCE_RECOVERY_AND_FINAL_QA_01/DEV'
$paths = [ordered]@{
    Session = "$stage/CORE_02/ReleaseSession.cs"
    Window = "$stage/UI_SR3_04_EDITOR/IntegratedWindow.cs"
    Populations = "$stage/UI_SR3_04_EDITOR/IntegratedPopulations.cs"
    Labels = "$stage/UI_SR3_04_EDITOR/IntegratedReviewLayout.cs"
    Project = "$stage/UI_SR3_04_EDITOR/Integrated.WinUI.csproj"
    Shell = 'dotnet/src/SistemaMarcaciones.Desktop.Centro.WinUI/InstitutionalShell.cs'
    Ingestion = 'dotnet/src/SistemaMarcaciones.Monthly.SourceIngestion/MonthlySourceIngestion.cs'
    Consumer = 'dotnet/src/SistemaMarcaciones.Infrastructure/Referencia/Phase4Candidate.cs'
    StyleConsumer = 'dotnet/src/SistemaMarcaciones.Infrastructure/Referencia/CatalogoEstilos.cs'
    Rules = 'src/marcaciones_cba/rules.py'
    Discipline = 'dotnet/src/SistemaMarcaciones.Monthly.Export.Eligibility/DisciplinePlan.cs'
    MonthlyReview = 'src/marcaciones_cba/monthly_review.py'
    Events = 'src/marcaciones_cba/novelty_events.py'
    Documents = 'src/marcaciones_cba/institutional_documents.py'
    Templates = 'src/marcaciones_cba/document_templates.py'
}
$sources = @()
$texts = @{}
foreach ($key in $paths.Keys) {
    $path = Join-Path $root $paths[$key]
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "REVIEW_SOURCE_MISSING:$key" }
    $info = Get-Item -LiteralPath $path
    if ($info.Length -gt 8MB) { throw "UNEXPECTED_SOURCE_SIZE:$key" }
    $before = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $texts[$key] = [IO.File]::ReadAllText($path)
    $after = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($before -cne $after) { throw "SOURCE_CHANGED_DURING_READ:$key" }
    $sources += [ordered]@{ Key=$key; RelativePath=$paths[$key]; SHA256=$before; Bytes=$info.Length }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($document)
try {
    $part = $archive.GetEntry('word/document.xml')
    if ($null -eq $part -or $part.Length -gt 8MB) { throw 'INVALID_DOCUMENT_PART' }
    $reader = [IO.StreamReader]::new($part.Open())
    try { $xmlText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $xmlReader = [Xml.XmlReader]::Create([IO.StringReader]::new($xmlText), $settings)
    $xml = [Xml.XmlDocument]::new()
    $xml.XmlResolver = $null
    try { $xml.Load($xmlReader) } finally { $xmlReader.Dispose() }
    $ns = [Xml.XmlNamespaceManager]::new($xml.NameTable)
    $ns.AddNamespace('w','http://schemas.openxmlformats.org/wordprocessingml/2006/main')
    $paragraphs = @($xml.SelectNodes('//w:p',$ns) | ForEach-Object {
        $value = ($_.SelectNodes('.//w:t',$ns) | ForEach-Object { $_.InnerText }) -join ''
        if ($value.Trim()) { $value }
    })
    if ($paragraphs.Count -ne 62) { throw 'PARAGRAPH_REFERENCE_COUNT_CHANGED' }
} finally { $archive.Dispose() }
$checks = @(
    @{Id='FIXTURE_REQUIRED'; Source='Session'; Pattern='!c\.TestFixtureOnly'},
    @{Id='QA_ROUTE_REQUIRED'; Source='Session'; Pattern='c\.Route!=B09Route\.NativeFixtureSandbox'},
    @{Id='QA_IDENTITIES_REQUIRED'; Source='Session'; Pattern='StartsWith\("SRV-QA-'},
    @{Id='PINNED_BIOMETRIC_IMPORT'; Source='Window'; Pattern='session\.ReimportExactQaBiometrics'},
    @{Id='PINNED_EXECUTION'; Source='Session'; Pattern='foreach\(var pin in Inner\.Configuration\.Inputs\)'},
    @{Id='SANDBOX_STARTUP'; Source='Window'; Pattern='EXPLICIT_INTEGRATED_SANDBOX_CATALOG_REQUIRED'},
    @{Id='QA_EXPORT_DESTINATION'; Source='Populations'; Pattern='TEST_FIXTURE_ONLY'},
    @{Id='ABSOLUTE_BUILD_PATHS'; Source='Project'; Pattern='C:/Users/'},
    @{Id='DISCIPLINE_PRESENTATION_GAP'; Source='Discipline'; Pattern='MULTI_REASON_PRESENTATION_NOT_INSTITUTIONALLY_APPROVED'},
    @{Id='LEGACY_MEDICAL_ORANGE'; Source='Rules'; Pattern='"permiso_medico": "F4B183"'},
    @{Id='LEGACY_OPADMIN_TWO_MARKS'; Source='Rules'; Pattern='OPERATIVE_ADMIN_EXPECTED_MIN_PUNCHES = 2'}
)
$observations = @(foreach ($check in $checks) {
    [ordered]@{Id=$check.Id;Source=$paths[$check.Source];Observed=[bool]($texts[$check.Source] -match $check.Pattern)}
})
# Recheck exact selected files at the end, without scanning institutional data.
foreach ($source in $sources) {
    if ((Get-FileHash -LiteralPath (Join-Path $root $source.RelativePath) -Algorithm SHA256).Hash -cne $source.SHA256) {
        throw ('SOURCE_CHANGED_BEFORE_REPORT:' + $source.Key)
    }
}
if ((Get-FileHash -LiteralPath $document -Algorithm SHA256).Hash -cne $documentHash) { throw 'DOCUMENT_CHANGED' }
$report = [ordered]@{
    Schema='CBA_DIRECTRICES_STATIC_REVIEW_V1'
    AtUTC=[DateTimeOffset]::UtcNow.ToString('o')
    Scope='STATIC_SELECTED_SOURCES_ONLY'
    DirectricesSHA256=$documentHash
    NonEmptyParagraphs=$paragraphs.Count
    Sources=$sources
    Observations=$observations
    ApplicationStarted=$false
    BuildExecuted=$false
    ProductionDataInspected=$false
    ReviewedFilesChanged=$false
    FunctionalAcceptance='NOT_EXECUTED'
    FinalDelivery='PENDING'
}
$parent = Split-Path -Parent $output
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'OUTPUT_PARENT_REQUIRED' }
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($report | ConvertTo-Json -Depth 10))
$stream = [IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try { $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
[pscustomobject]@{ Output=$output; SHA256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash; Sources=$sources.Count; Observed=@($observations | Where-Object Observed).Count; FinalDelivery='PENDING' }
