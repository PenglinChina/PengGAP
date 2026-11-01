set output=./publish
if exist "%output%" rd /S /Q "%output%"
dotnet publish -c Release -o "%output%/penggap_win-x64" ./PengGAP.UI/PengGAP.UI.csproj
dotnet publish -c Release /p:PublishSingleFile=true --self-contained -r win-x64 -o "%output%/penggap_win-x64" ./PengGAP/PengGAP.csproj
dotnet publish -c Release /p:PublishSingleFile=true --self-contained -r linux-x64 -o "%output%/penggap_linux-x64" ./PengGAP/PengGAP.csproj
dotnet publish -c Release /p:PublishSingleFile=true --self-contained -r linux-arm64 -o "%output%/penggap_linux-arm64" ./PengGAP/PengGAP.csproj
dotnet publish -c Release /p:PublishSingleFile=true --self-contained -r osx-x64 -o "%output%/penggap_osx-x64" ./PengGAP/PengGAP.csproj
dotnet publish -c Release /p:PublishSingleFile=true --self-contained -r osx-arm64 -o "%output%/penggap_osx-arm64" ./PengGAP/PengGAP.csproj