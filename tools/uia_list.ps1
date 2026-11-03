Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Window)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
foreach ($w in $wins) {
    $name = $w.Current.Name
    $pid_ = $w.Current.ProcessId
    Write-Output ("WIN: '{0}' pid={1} class={2}" -f $name, $pid_, $w.Current.ClassName)
    $bcond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $btns = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bcond)
    foreach ($b in $btns) { Write-Output ("   BTN: '{0}'" -f $b.Current.Name) }
}
