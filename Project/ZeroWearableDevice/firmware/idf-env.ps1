# Activate ESP-IDF v5.5.5 (installed by eim into C:\esp\v5.5.5) in the current PowerShell session.
#   . .\idf-env.ps1
#   idf.py -p COM7 build flash monitor
$env:IDF_TOOLS_PATH = "C:\esp\v5.5.5\tools"
# The IDF venv was created with Python 3.12 (eim 0.1.7 skipped this step); pin it so export.ps1
# doesn't look for a venv matching whatever `python` is first on PATH.
$env:IDF_PYTHON_ENV_PATH = "C:\esp\v5.5.5\tools\python_env\idf5.5_py3.12_env"
if (-not $env:WS_AMOLED_REPO) { $env:WS_AMOLED_REPO = "C:\esp\ws-amoled-175c" }   # Waveshare board repo (brookesia_core)
. C:\esp\v5.5.5\esp-idf\export.ps1 *> $null
Write-Host "ESP-IDF $(idf.py --version)  |  WS_AMOLED_REPO=$env:WS_AMOLED_REPO"
