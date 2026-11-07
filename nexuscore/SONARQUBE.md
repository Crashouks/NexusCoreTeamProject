# SonarQube analysis

The API targets .NET 10. Run the .NET scanner around the solution build so C# analysis is
bound to the compiled projects. Do not put a Sonar token in this file, source code, or Git.

## Local SonarQube Community Build

1. Start Docker Desktop and run the local server using the
   [official Community Build instructions](https://docs.sonarsource.com/sonarqube-community-build/try-out-sonarqube).
   The server defaults to `http://localhost:9000`.
2. Create a project and generate a token in the SonarQube UI.
3. From PowerShell, in the `nexuscore` directory, install the scanner once and run:

   ```powershell
   dotnet tool install --global dotnet-sonarscanner
   $env:SONAR_TOKEN = '<token created in SonarQube>'
   dotnet sonarscanner begin /k:"NexusCoreTeamProject" /d:sonar.host.url="http://localhost:9000" /d:sonar.token="$env:SONAR_TOKEN"
   dotnet build .\NexusCore.slnx
   dotnet test .\NexusCore.Api.Tests\NexusCore.Api.Tests.csproj --no-build
   dotnet sonarscanner end /d:sonar.token="$env:SONAR_TOKEN"
   Remove-Item Env:SONAR_TOKEN
   ```

   For SonarQube Cloud, use the project key and organization assigned by the service and set
   `sonar.host.url` to `https://sonarcloud.io`. Do not use the local project key as a cloud
   project key unless the organization configured it that way.

## Review checklist

Inspect Bugs, Code Smells, Vulnerabilities, Security Hotspots, and coverage separately. Treat
hotspots as items requiring human review, and do not infer coverage when no coverage report was
provided. The local analyzer cannot be run in the current workspace because Docker is unavailable;
record the actual dashboard counts and reviewed findings here after a scan rather than estimating
or copying example values.
