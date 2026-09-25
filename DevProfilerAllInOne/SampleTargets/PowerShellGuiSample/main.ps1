[CmdletBinding()]
param(
    [string]$Title = "DevProfiler PowerShell GUI Test"
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

$apartmentState = [Threading.Thread]::CurrentThread.GetApartmentState()
if ($apartmentState -ne [Threading.ApartmentState]::STA) {
    throw "PowerShell GUI sample requires STA, but the current apartment state is $apartmentState."
}

$form = [System.Windows.Forms.Form]::new()
$form.Text = $Title
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.ClientSize = [System.Drawing.Size]::new(520, 220)
$form.MinimumSize = [System.Drawing.Size]::new(420, 200)

$label = [System.Windows.Forms.Label]::new()
$label.AutoSize = $false
$label.Dock = [System.Windows.Forms.DockStyle]::Fill
$label.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$label.Font = [System.Drawing.Font]::new("Segoe UI", 12)
$label.Text = "PowerShell WinForms is running under DevProfiler in $apartmentState mode.`r`nClose this window to complete the profiling session."

$closeButton = [System.Windows.Forms.Button]::new()
$closeButton.Text = "Close"
$closeButton.Dock = [System.Windows.Forms.DockStyle]::Bottom
$closeButton.Height = 44
$closeButton.Add_Click({ $form.Close() })

$form.Controls.Add($label)
$form.Controls.Add($closeButton)

Write-Information "Opening PowerShell WinForms test window." -InformationAction Continue
$result = $form.ShowDialog()
Write-Output "PowerShell WinForms test closed with result: $result"

$form.Dispose()
