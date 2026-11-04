@echo off
setlocal

python -I "%~dp0verify-sigs.py" %*
set RESULT=%errorlevel%

echo.
echo ========================================
if %RESULT%==0 (
    echo   RESULT: SIGS SAME - all signatures still match
) else if %RESULT%==1 (
    echo   RESULT: SIGS CHANGED - one or more signatures no longer match
) else (
    echo   RESULT: ERROR - could not run the check, see above
)
echo ========================================
echo.
pause
exit /b %RESULT%
