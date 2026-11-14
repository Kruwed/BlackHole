# Developers' Guide for debugging Black Hole

This guide is for giving detailed instructions about how to debug the components of Black Hole to those developers who want to contribute to the project.
We will focus on how to achieve this with `vscode` first because that is the cross-platform IDE used by some of the developer team members.

## Before Starting
We assume the reader has already read the project [README](../../README.md) file and has installed the [.NET 10.0 SDK](https://dotnet.microsoft.com/download), and knows how to clone the repository and build the solution.


## Install VS Code and C# extension
### 1: Get Visual Studio Code
Install Visual Studio Code (VSC).
Pick the latest VSC version from here: [https://code.visualstudio.com/download](https://code.visualstudio.com/download)


### 2: Install C# Extension
Open the command palette in VS Code (press <kbd>F1</kbd>) and type `ext install C#` to trigger the installation of the extension.
VS Code will show a message that the extension has been installed and it will restart.
Installing this extension can take a while.


### 3: Open the BlackHole directory in VS Code
Go to `File->Open Folder` and open the BlackHole directory in Visual Studio Code.

After this step we are ready to start configuring the build tasks and the launches.

## Understanding the components

Black Hole is the desktop application that users download and install on their machines.
Additionally, the solution contains other components like the daemon, the coordinator, the tests, and others.

### Client

Let us start by creating the launcher (it goes into the `.vscode/launch.json` file).
This file contains the list of projects that can be launched, how to do it, what tasks have to be executed before the launching, etc.

```json
{
   "version": "0.2.0",
   "configurations": [{
      "name": "Black Hole GUI .NET Core",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build-client",
      "program": "${workspaceFolder}/WalletWasabi.Fluent.Desktop/bin/Debug/net10.0/WalletWasabi.Fluent.Desktop.dll",
      "args": [],
      "cwd": "${workspaceFolder}/WalletWasabi.Fluent.Desktop",
      "stopAtEntry": false,
      "internalConsoleOptions": "openOnSessionStart",
      "sourceFileMap": {
          ".": "${workspaceFolder}"
      }
   }]
}
```


It is important to see that this launcher requires the execution of a `build-client` task.
That is defined in the tasks file (it goes into `.vscode/tasks.json` file).

```json
{
    "version": "2.0.0",
    "tasks": [{
        "label": "build-client",
        "command": "dotnet",
        "type": "process",
        "args": [
           "build",
           "${workspaceFolder}/WalletWasabi.Fluent.Desktop/WalletWasabi.Fluent.Desktop.csproj"
        ],
        "problemMatcher": "$msCompile"
    }]
}
```

After these two files are created press (CTRL+SHIFT+D) to go to the debugger, set a couple of breakpoints, select the `Black Hole GUI .NET Core` launcher (it should be the only one available) and press the play button.
That is all.

### Files

#### .vscode/launch.json

```json

{
    // Use IntelliSense to learn about possible attributes.
    // Hover to view descriptions of existing attributes.
    // For more information, visit: https://go.microsoft.com/fwlink/?linkid=830387
    "version": "0.2.0",
    "configurations": [
        {
            "name": "Black Hole GUI .NET Core",
            "type": "coreclr",
            "request": "launch",
            "preLaunchTask": "build-client",
            "program": "${workspaceFolder}/WalletWasabi.Fluent.Desktop/bin/Debug/net10.0/WalletWasabi.Fluent.Desktop.dll",
            "args": [],
            "cwd": "${workspaceFolder}/WalletWasabi.Fluent.Desktop",
            "stopAtEntry": false,
            "internalConsoleOptions": "openOnSessionStart",
            "sourceFileMap": {
                ".": "${workspaceFolder}"
            }
        },
        {
            "name": "Black Hole Daemon .NET Core",
            "type": "coreclr",
            "request": "launch",
            "preLaunchTask": "build-all",
            "program": "${workspaceFolder}/WalletWasabi.Daemon/bin/Debug/net10.0/WalletWasabi.Daemon.dll",
            "args": [
                "--network=regtest", "--loadwallet"
            ],
            "cwd": "${workspaceFolder}",
            "stopAtEntry": false,
            "console": "internalConsole",
            "sourceFileMap": {
                ".": "${workspaceFolder}"
            }
        },
        {
            "name": "Black Hole Unit Tests .NET Core",
            "type": "coreclr",
            "request": "launch",
            "preLaunchTask": "build-all",
            "program": "dotnet",
            "args": [
                "test", "--project", "WalletWasabi.Tests/WalletWasabi.Tests.csproj", "--filter-namespace", "*UnitTests*"
            ],
            "cwd": "${workspaceFolder}",
            "stopAtEntry": false,
            "console": "internalConsole"
        },
        {
            "name": ".NET Core Attach",
            "type": "coreclr",
            "request": "attach",
            "processId": "${command:pickProcess}"
        }
    ]
}
```


#### .vscode/tasks.json

```json
{
    "version": "2.0.0",
    "tasks": [{
        "label": "build-client",
        "command": "dotnet",
        "type": "process",
        "args": [
            "build",
            "${workspaceFolder}/WalletWasabi.Fluent.Desktop/WalletWasabi.Fluent.Desktop.csproj",
            "/property:GenerateFullPaths=true",
            "/consoleloggerparameters:NoSummary"
            ],
            "problemMatcher": "$msCompile"
        },
        {
            "label": "build-all",
            "command": "dotnet",
            "type": "process",
            "args": [
                "build",
                "${workspaceFolder}",
                "/property:GenerateFullPaths=true",
                "/consoleloggerparameters:NoSummary"
                ],
            "problemMatcher": "$msCompile"
        },
        {
            "label": "watch",
            "command": "dotnet",
            "type": "process",
            "args": [
                "watch",
                "run",
                "${workspaceFolder}/WalletWasabi.Fluent.Desktop/WalletWasabi.Fluent.Desktop.csproj",
                "/property:GenerateFullPaths=true",
                "/consoleloggerparameters:NoSummary"
            ],
            "problemMatcher": "$msCompile"
        }
    ]
}
```

## Debugging code with unit tests

Because `dotnet test` will run the test code in a child process, it isn't possible to configure a "unit test debugging" configuration in `launch.json`.
You can tweak debugging options for unit tests by addind the following setting to your local `.vscode/settings.json` file (if it doesn't exists then create it):

```json
   "csharp.unitTestDebuggingOptions": {
        "sourceFileMap": {
            "WalletWasabi/": "${workspaceFolder}/WalletWasabi",
            "WalletWasabi.Fluent.Desktop/": "${workspaceFolder}/WalletWasabi.Fluent.Desktop",
            "WalletWasabi.Tests/": "${workspaceFolder}/WalletWasabi.Tests"
        }
    },
```

## How to setup and run the whole system

Sometimes, as developers, we need to test an advanced interaction between the Bitcoin Core node and the wallet.
Just imagine a case in which we want to verify the system behavior after a blockchain reorg.
In these cases we have to setup and run all the components, and use regtest.

### Install Bitcoin Core

Download and install Bitcoin Core from [https://bitcoincore.org/bin/](https://bitcoincore.org/bin/).

### Get some coins to test

Once Bitcoin Core and Black Hole are running on regtest (see [SetupRegtest](../SetupRegtest.md)), we can mine some blocks and get the new mined coins into our wallet.

1. Go to Black Hole and generate a new address
2. Open a terminal and type `bitcoin-cli -regtest generatetoaddress 101 <your-address>`

After this we should receive our first 50 bitcoins!
