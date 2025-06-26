# Path to the appsettings.json file (adjust as needed)
$jsonPath = ".\appsettings.json"

# Path to the IpConfigUtility executable folder
$toolPath = "C:\\Program Files\\Lucid Vision Labs\\Arena SDK\\x64Release"
$toolExe = "IpConfigUtility_v140.exe"

# Read the JSON file as raw text
$rawJson = Get-Content -Path $jsonPath -Raw

# Remove comments (// followed by anything until the end of the line)
$cleanJson = $rawJson -replace '(?m)^\s*//.*$|//.*$', ''

# Parse the cleaned JSON
try {
    $settings = $cleanJson | ConvertFrom-Json
} catch {
    Write-Host "Error parsing JSON: $_"
    exit
}

# Subnet mask (hardcoded since it's not in the JSON)
$subnetMask = "255.255.255.0"

# Array to store all commands
$commands = @()

# Check if the Cameras array exists
if ($settings.CameraOptions -and $settings.CameraOptions.Cameras) {
    foreach ($camera in $settings.CameraOptions.Cameras) {
        $macAddress = $camera.MacAddress
        $ipAddress = $camera.CameraAddress
        $gateway = $camera.ReceiverAddress

        if (-not $macAddress -or -not $ipAddress -or -not $gateway) {
            Write-Host "Skipping camera $($camera.Name): Missing required fields (MacAddress, CameraAddress, or ReceiverAddress)"
            continue
        }

        # Build the command
        $command = ".\$toolExe /force -m $macAddress -a $ipAddress -s $subnetMask -g $gateway"

        # Store in array
        $commands += $command
    }
} else {
    Write-Host "No cameras found in appsettings.json under CameraOptions.Cameras"
    exit
}

# Change to tool directory
Set-Location -Path $toolPath

# Execute each command
foreach ($cmd in $commands) {
    Write-Host "Executing: $cmd"
    Invoke-Expression $cmd
}
