@echo off
setlocal
cd /d "%~dp0"
echo.
echo This records the exact UDP audio transmitted by Production XIAO.
echo Starting a 30-second capture immediately.
echo The window shows a live received-frame count every second.
"C:\Users\anton\AppData\Local\Programs\Python\Python314\python.exe" "%~dp0capture_device_audio.py" --seconds 30 --output "%~dp0device-capture.wav"
echo.
echo Capture complete. The WAV is in this folder as device-capture.wav.
pause
