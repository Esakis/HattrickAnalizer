@echo off
setlocal
echo === HattrickAnalizer ===

REM Zwalniamy tylko porty aplikacji (5000/4200) zamiast zabijac wszystkie
REM procesy node.exe/dotnet.exe - to ubijaloby tez inne aplikacje na komputerze.
echo Zwalnianie portu 5000 (backend) i 4200 (frontend)...
for /f "tokens=5" %%p in ('netstat -ano ^| findstr ":5000 " ^| findstr "LISTENING"') do taskkill /F /PID %%p >nul 2>nul
for /f "tokens=5" %%p in ('netstat -ano ^| findstr ":4200 " ^| findstr "LISTENING"') do taskkill /F /PID %%p >nul 2>nul

if not exist "%~dp0Frontend\node_modules" (
    echo Brak node_modules - instalowanie zaleznosci frontendu...
    pushd "%~dp0Frontend"
    call npm install --no-audit --no-fund
    popd
)

echo Uruchamianie backendu (.NET 8)...
start "HattrickAnalizer Backend" cmd /k "cd /d %~dp0Backend && dotnet run"

echo Uruchamianie frontendu (Angular)...
start "HattrickAnalizer Frontend" cmd /k "cd /d %~dp0Frontend && npm start"

echo.
echo Backend:  http://localhost:5000   (Swagger: http://localhost:5000/swagger)
echo Frontend: http://localhost:4200
echo.
echo Nacisnij dowolny klawisz, aby zamknac to okno...
pause >nul
