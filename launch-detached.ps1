#requires -Version 7.0
function Start-QuotaDetached {
    param([Parameter(Mandatory)][string]$Executable, [string]$Arguments = '')
    $ErrorActionPreference = 'Stop'
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    $root = $service.GetFolder('\')
    $taskName = 'CodexQuotaMeter-Launch-' + [guid]::NewGuid().ToString('N')
    $registered = $null
    try {
        $definition = $service.NewTask(0)
        $definition.RegistrationInfo.Description = 'Start Codex Quota Meter independently of the installer terminal.'
        $definition.Settings.Enabled = $true
        $definition.Settings.Hidden = $true
        $definition.Settings.DisallowStartIfOnBatteries = $false
        $definition.Settings.StopIfGoingOnBatteries = $false
        $definition.Settings.ExecutionTimeLimit = 'PT0S'
        $definition.Principal.UserId = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $definition.Principal.LogonType = 3 # Current user's interactive session, without a password.
        $definition.Principal.RunLevel = 0
        $action = $definition.Actions.Create(0)
        $action.Path = [IO.Path]::GetFullPath($Executable)
        $action.WorkingDirectory = Split-Path -Parent $action.Path
        $action.Arguments = $Arguments
        $registered = $root.RegisterTaskDefinition($taskName, $definition, 2,
            $definition.Principal.UserId, $null, 3, $null)
        $null = $registered.Run($null)
        $deadline = [DateTime]::UtcNow.AddSeconds(8)
        do {
            Start-Sleep -Milliseconds 100
            if ($registered.State -eq 4) { return }
            if ($registered.LastRunTime.Year -gt 2000 -and $registered.State -eq 3) {
                if ($registered.LastTaskResult -ne 0) {
                    throw ('系统启动失败：0x{0:X8}' -f $registered.LastTaskResult)
                }
                return
            }
        } while ([DateTime]::UtcNow -lt $deadline)
        throw '系统启动超时。'
    } finally {
        # Deleting the temporary definition leaves its launched process running.
        if ($registered) { $root.DeleteTask($taskName, 0) }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($root) | Out-Null
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($service) | Out-Null
    }
}
