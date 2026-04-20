@echo off
echo Uruchamianie aplikacji HattrickAnalizer...

echo Zabijanie istniejacych procesow...

taskkill /F /IM "ng.exe" 2>nul
taskkill /F /IM "node.exe" 2>nul
taskkill /F /IM "dotnet.exe" 2>nul

echo Czekanie 3 sekundy na zakonczenie procesow...
timeout /t 3 /nobreak >nul

echo Uruchamianie backendu (.NET 8.0)...
start "Backend" cmd /k "cd /d %~dp0Backend && dotnet run"

echo Czekanie 5 sekund na uruchomienie backendu...
timeout /t 5 /nobreak >nul

echo Uruchamianie frontendu (Angular)...
start "Frontend" cmd /k "cd /d %~dp0Frontend && yarn start"

echo Aplikacja zostala uruchomiona!
echo Backend: https://localhost:7000 (lub inne porty z konfiguracji)
echo Frontend: http://localhost:4200
echo Nacisnij dowolny klawisz, aby zamknac to okno...
pause >nul
