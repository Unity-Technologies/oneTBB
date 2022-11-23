@ECHO OFF
REM
REM Copyright (c) 2022 Unity Technologies
REM
REM Licensed under the Apache License, Version 2.0 (the "License");
REM you may not use this file except in compliance with the License.
REM You may obtain a copy of the License at
REM
REM     http://www.apache.org/licenses/LICENSE-2.0
REM
REM Unless required by applicable law or agreed to in writing, software
REM distributed under the License is distributed on an "AS IS" BASIS,
REM WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
REM See the License for the specific language governing permissions and
REM limitations under the License.
REM

REM Run robocopy and return 0 on success, nonzero on failure.
REM Robocopy exit codes 0-7 mean success, anything else means failure. References:
REM https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/robocopy
REM https://ss64.com/nt/robocopy-exit.html

ROBOCOPY.EXE %*
IF %ERRORLEVEL% LSS 0 EXIT %ERRORLEVEL%
IF %ERRORLEVEL% LSS 8 EXIT 0
EXIT %ERRORLEVEL%