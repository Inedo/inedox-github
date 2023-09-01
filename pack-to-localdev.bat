@echo off

dotnet new tool-manifest --force
dotnet tool install inedo.extensionpackager

cd GitHub\InedoExtension
dotnet inedoxpack pack . C:\LocalDev\BuildMaster\Extensions\GitHub.upack --build=Debug -o
cd ..\..