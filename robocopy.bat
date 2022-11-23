@ECHO OFF

REM Run robocopy and return 0 on success, nonzero on failure.
REM Robocopy exit codes 0-7 mean success, anything else means failure. References:
REM https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/robocopy
REM https://ss64.com/nt/robocopy-exit.html

ROBOCOPY.EXE %*
IF %ERRORLEVEL% LSS 0 EXIT %ERRORLEVEL%
IF %ERRORLEVEL% LSS 8 EXIT 0
EXIT %ERRORLEVEL%