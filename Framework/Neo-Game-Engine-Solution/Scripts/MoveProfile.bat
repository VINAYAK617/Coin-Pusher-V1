@echo off

set "solutionDir=%~1"
set "projectDir=%~2"
set "configuration=%~3"
set "dotNetFolder=%~4"
set "isUI=%~5"

setlocal enabledelayedexpansion

set "dotNetFullFolder="

rem /i ignore the case used for isUI, can be true, TRUE, TrUe...
if /i "%isUI%"=="true" (
    set "dotNetFullFolder=%dotNetFolder%-windows"
) else (
    set "dotNetFullFolder=%dotNetFolder%"
)

rem Define the path where the profiles will be copied to
set "destinationPath=%projectDir%\bin\%configuration%\!dotNetFullFolder!\Profiles"

rem delete existing folder if it exists
IF EXIST "!destinationPath!" (
    rmdir /s /q "!destinationPath!"
)
 
mkdir "!destinationPath!"

rem Define the source path for the profiles
set "sourcePath=%solutionDir%\Profiles"

rem Loop through folders in the specified path
for /d %%d in ("%sourcePath%\*") do (
    rem Check if a .csproj file exists in the current folder
    for %%f in ("%%d\*.csproj") do (
        rem Get the name of the .csproj file without the extension
        set "profileName=%%~nf"

        rem copy dll
        set "dllSourceFile=%%d\bin\!configuration!\!dotNetFolder!\!profileName!.dll"
        
        if exist "!dllSourceFile!" (
            copy "!dllSourceFile!" "!destinationPath!"
        )
        
        rem copy dependencies
        set "depSourceFile=%%d\bin\!configuration!\!dotNetFolder!\!profileName!.deps.json"
        
        if exist "!depSourceFile!" (
            copy "!depSourceFile!" "!destinationPath!"
        )
    )
)


endlocal