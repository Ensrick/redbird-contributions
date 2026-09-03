param(
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
[xml]$source = Get-Content -LiteralPath $InputPath -Raw
$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)
$writer = [System.Xml.XmlWriter]::Create($OutputPath, $settings)

try {
    $writer.WriteStartDocument()
    $writer.WriteStartElement('testsuites')

    foreach ($assembly in $source.assemblies.assembly) {
        $writer.WriteStartElement('testsuite')
        $writer.WriteAttributeString('name', [System.IO.Path]::GetFileName([string]$assembly.name))
        $writer.WriteAttributeString('tests', [string]$assembly.total)
        $writer.WriteAttributeString('failures', [string]$assembly.failed)
        $writer.WriteAttributeString('errors', [string]$assembly.errors)
        $writer.WriteAttributeString('skipped', [string]$assembly.skipped)
        $writer.WriteAttributeString('time', [string]$assembly.time)

        foreach ($collection in $assembly.collection) {
            foreach ($test in $collection.test) {
                $writer.WriteStartElement('testcase')
                $writer.WriteAttributeString('classname', [string]$test.type)
                $writer.WriteAttributeString('name', [string]$test.method)
                $writer.WriteAttributeString('time', [string]$test.time)

                if ($test.result -eq 'Fail') {
                    $writer.WriteStartElement('failure')
                    $writer.WriteAttributeString('type', [string]$test.failure.'exception-type')
                    $writer.WriteAttributeString('message', [string]$test.failure.message.InnerText)
                    $writer.WriteString([string]$test.failure.'stack-trace'.InnerText)
                    $writer.WriteEndElement()
                }
                elseif ($test.result -eq 'Skip') {
                    $writer.WriteStartElement('skipped')
                    $writer.WriteAttributeString('message', [string]$test.reason.InnerText)
                    $writer.WriteEndElement()
                }

                $writer.WriteEndElement()
            }
        }

        $writer.WriteEndElement()
    }

    $writer.WriteEndElement()
    $writer.WriteEndDocument()
}
finally {
    $writer.Dispose()
}
