# TsukiAI Migration PowerShell Script
# This script renames PersonalAiOverlay to TsukiAI

Write-Host "=== TsukiAI Migration Script ===" -ForegroundColor Cyan

# Step 1: Create backup
$backupDir = "../PersonalAiOverlay_Backup_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
Write-Host "Creating backup at $backupDir..." -ForegroundColor Yellow
Copy-Item -Path "." -Destination $backupDir -Recurse -Force

# Step 2: Rename solution file
Write-Host "Renaming solution file..." -ForegroundColor Green
if (Test-Path "PersonalAiOverlay.sln") {
    Rename-Item "PersonalAiOverlay.sln" "TsukiAI.sln"
    (Get-Content "TsukiAI.sln") -replace 'PersonalAiOverlay', 'TsukiAI' | Set-Content "TsukiAI.sln"
}

# Step 3: Rename project directories
Write-Host "Renaming project directories..." -ForegroundColor Green
if (Test-Path "PersonalAiOverlay.Core") {
    Rename-Item "PersonalAiOverlay.Core" "TsukiAI.Core"
}
if (Test-Path "PersonalAiOverlay.App") {
    Rename-Item "PersonalAiOverlay.App" "TsukiAI.Desktop"
}

# Step 4: Rename project files
Write-Host "Renaming project files..." -ForegroundColor Green
$coreProj = "TsukiAI.Core/PersonalAiOverlay.Core.csproj"
$desktopProj = "TsukiAI.Desktop/PersonalAiOverlay.App.csproj"

if (Test-Path $coreProj) {
    Rename-Item $coreProj "TsukiAI.Core.csproj"
}
if (Test-Path $desktopProj) {
    Rename-Item $desktopProj "TsukiAI.Desktop.csproj"
}

# Step 5: Update namespaces in all C# files
Write-Host "Updating namespaces in C# files..." -ForegroundColor Green
$csFiles = Get-ChildItem -Path "." -Filter "*.cs" -Recurse | Where-Object { $_.FullName -notlike "*\obj\*" -and $_.FullName -notlike "*\bin\*" }

foreach ($file in $csFiles) {
    $content = Get-Content $file.FullName -Raw
    $original = $content
    
    # Replace namespaces
    $content = $content -replace 'namespace PersonalAiOverlay\.Core', 'namespace TsukiAI.Core'
    $content = $content -replace 'namespace PersonalAiOverlay\.App', 'namespace TsukiAI.Desktop'
    $content = $content -replace 'using PersonalAiOverlay\.Core', 'using TsukiAI.Core'
    $content = $content -replace 'using PersonalAiOverlay\.App', 'using TsukiAI.Desktop'
    
    # Replace project references
    $content = $content -replace 'PersonalAiOverlay\.Core', 'TsukiAI.Core'
    $content = $content -replace 'PersonalAiOverlay\.App', 'TsukiAI.Desktop'
    
    if ($content -ne $original) {
        Set-Content $file.FullName $content -NoNewline
        Write-Host "  Updated: $($file.FullName)" -ForegroundColor DarkGray
    }
}

# Step 6: Update XAML files
Write-Host "Updating XAML files..." -ForegroundColor Green
$xamlFiles = Get-ChildItem -Path "." -Filter "*.xaml" -Recurse | Where-Object { $_.FullName -notlike "*\obj\*" -and $_.FullName -notlike "*\bin\*" }

foreach ($file in $xamlFiles) {
    $content = Get-Content $file.FullName -Raw
    $original = $content
    
    $content = $content -replace 'PersonalAiOverlay\.Core', 'TsukiAI.Core'
    $content = $content -replace 'PersonalAiOverlay\.App', 'TsukiAI.Desktop'
    $content = $content -replace 'PersonalAiOverlay', 'TsukiAI'
    
    if ($content -ne $original) {
        Set-Content $file.FullName $content -NoNewline
        Write-Host "  Updated: $($file.FullName)" -ForegroundColor DarkGray
    }
}

# Step 7: Update project files
Write-Host "Updating project references..." -ForegroundColor Green
$projFiles = Get-ChildItem -Path "." -Filter "*.csproj" -Recurse

foreach ($file in $projFiles) {
    $content = Get-Content $file.FullName -Raw
    $original = $content
    
    $content = $content -replace 'PersonalAiOverlay\.Core', 'TsukiAI.Core'
    $content = $content -replace 'PersonalAiOverlay\.App', 'TsukiAI.Desktop'
    
    if ($content -ne $original) {
        Set-Content $file.FullName $content -NoNewline
    }
}

# Step 8: Update README and documentation
Write-Host "Updating documentation..." -ForegroundColor Green
$mdFiles = Get-ChildItem -Path "." -Filter "*.md" -Recurse

foreach ($file in $mdFiles) {
    $content = Get-Content $file.FullName -Raw
    $original = $content
    
    $content = $content -replace 'PersonalAiOverlay', 'TsukiAI'
    $content = $content -replace 'Personal AI', 'TsukiAI'
    
    if ($content -ne $original) {
        Set-Content $file.FullName $content -NoNewline
    }
}

Write-Host "" 
Write-Host "=== Migration Complete! ===" -ForegroundColor Cyan
Write-Host "Backup created at: $backupDir" -ForegroundColor Yellow
Write-Host "" 
Write-Host "Next steps:" -ForegroundColor Green
Write-Host "1. Open TsukiAI.sln in Visual Studio"
Write-Host "2. Clean solution (Build > Clean Solution)"
Write-Host "3. Rebuild solution (Build > Rebuild Solution)"
Write-Host "4. Run the application"
