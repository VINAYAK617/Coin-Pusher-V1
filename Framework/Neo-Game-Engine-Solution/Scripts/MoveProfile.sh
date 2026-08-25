#!/bin/bash

solutionDir="$1"
projectDir="$2"
configuration="$3"
dotNetFolder="$4"
isUI="$5"

# On macOS, always use net5.0 because the Launcher expects:
# bin/Debug/net5.0/Profiles
dotNetFullFolder="$dotNetFolder"

destinationPath="$projectDir/bin/$configuration/$dotNetFullFolder/Profiles"

echo "Creating Profiles folder at:"
echo "$destinationPath"

rm -rf "$destinationPath"
mkdir -p "$destinationPath"

sourcePath="$solutionDir/Profiles"

for dir in "$sourcePath"/*/; do
    [ -d "$dir" ] || continue

    for csproj in "$dir"/*.csproj; do
        [ -f "$csproj" ] || continue

        profileName="$(basename "$csproj" .csproj)"

        dllSourceFile="$dir/bin/$configuration/$dotNetFolder/$profileName.dll"
        depSourceFile="$dir/bin/$configuration/$dotNetFolder/$profileName.deps.json"

        if [ -f "$dllSourceFile" ]; then
            echo "Copying $profileName.dll"
            cp "$dllSourceFile" "$destinationPath/"
        else
            echo "Missing DLL: $dllSourceFile"
        fi

        if [ -f "$depSourceFile" ]; then
            echo "Copying $profileName.deps.json"
            cp "$depSourceFile" "$destinationPath/"
        else
            echo "Missing deps.json: $depSourceFile"
        fi
    done
done