@echo off
setlocal
cd /d "%~dp0"

rem Double-click this file to open the IAP Uploader window.

set "PY="
for %%C in (py python python3) do (
  if not defined PY (
    %%C -c "import sys" >nul 2>nul && set "PY=%%C"
  )
)

if not defined PY (
  echo No working Python was found. The "python" command may only be the Microsoft Store shortcut.
  echo Install Python from https://www.python.org/downloads/ and tick "Add python.exe to PATH",
  echo or turn off the Store shortcuts in Settings ^> Apps ^> Advanced app settings ^> App execution aliases.
  echo.
  pause
  exit /b 1
)

echo Using: %PY%
%PY% -c "import sys; print(sys.executable, sys.version.split()[0])"

%PY% -c "import tkinter, requests, jwt, google.auth, azure.identity, azure.keyvault.secrets" >nul 2>nul
if not errorlevel 1 goto checkaz

echo Installing the packages the upload tools need (first run only)...
%PY% -m pip install requests "pyjwt[crypto]" google-auth azure-identity azure-keyvault-secrets
%PY% -c "import tkinter, requests, jwt, google.auth, azure.identity, azure.keyvault.secrets"
if errorlevel 1 (
  echo.
  echo Something is still missing, see the error above.
  echo If it says "No module named tkinter", re-run the Python installer,
  echo choose Modify, and tick "tcl/tk and IDLE".
  echo.
  pause
  exit /b 1
)

:checkaz
where az >nul 2>nul
if not errorlevel 1 goto launch

echo.
echo The Azure CLI is not installed. Installing it now.
echo A Windows permission prompt may appear; click Yes.
echo.
where winget >nul 2>nul
if errorlevel 1 goto nowinget
winget install -e --id Microsoft.AzureCLI --accept-package-agreements --accept-source-agreements

rem The installer can't change this window's PATH, so add its usual folders here.
set "PATH=%PATH%;%ProgramFiles%\Microsoft SDKs\Azure\CLI2\wbin;%ProgramFiles(x86)%\Microsoft SDKs\Azure\CLI2\wbin"
where az >nul 2>nul
if not errorlevel 1 goto launch
echo.
echo The Azure CLI install did not finish. Install it from
echo https://aka.ms/installazurecliwindows then double-click this file again.
echo.
pause
exit /b 1

:nowinget
echo.
echo winget is not available on this PC. Install the Azure CLI from
echo https://aka.ms/installazurecliwindows then double-click this file again.
echo.
pause
exit /b 1

:launch
%PY% upload_gui.py
if errorlevel 1 (
  echo.
  echo The uploader window failed to start. The error is above.
  echo.
  pause
)
exit /b 0
