<#
Runs MyriaServer in Production mode so it picks up appsettings.Production.json
(real DB path, real secrets, the 0.0.0.0 Kestrel bind, and the real Realms URLs) instead of
the localhost-only dev defaults in appsettings.json. The "dotnet run" / Visual Studio launch
profiles always force ASPNETCORE_ENVIRONMENT=Development, so don't use those for real hosting.

Usage: just run this script. Leave the window open (or run it via Task Scheduler / as a
Windows Service if you want it to survive logoff and start automatically with your PC).
#>

$env:ASPNETCORE_ENVIRONMENT = "Production"
dotnet run --project (Join-Path $PSScriptRoot "MyriaServer.csproj") --configuration Release --no-launch-profile
