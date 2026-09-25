[CmdletBinding()]
param(
    [int]$Iterations = 4,
    [switch]$ShowWarning
)

function Invoke-Workload {
    param([int]$Round)

    Write-Verbose "Starting workload round $Round"
    $sum = 0.0
    for ($index = 1; $index -le 180000; $index++) {
        $sum += [Math]::Sqrt($index + $Round)
    }
    Start-Sleep -Milliseconds 120
    [pscustomobject]@{
        Round = $Round
        Sum = [Math]::Round($sum, 2)
    }
}

Write-Information "PowerShell sample started" -InformationAction Continue
for ($round = 1; $round -le $Iterations; $round++) {
    Invoke-Workload -Round $round -Verbose
}

if ($ShowWarning) {
    Write-Warning "This is an intentional sample warning."
}

Write-Output "PowerShell sample complete."
