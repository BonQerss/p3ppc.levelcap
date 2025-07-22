# Set Working Directory
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

Remove-Item "$env:RELOADEDIIMODS/p3ppc.levelcap/*" -Force -Recurse
dotnet publish "./p3ppc.levelcap.csproj" -c Release -o "$env:RELOADEDIIMODS/p3ppc.levelcap" /p:OutputPath="./bin/Release" /p:ReloadedILLink="true"

# Restore Working Directory
Pop-Location