# SPRDClientCore
> A powerful and feature-rich c# client library for communicating with Unisoc(Spreadtrum) devices. SPRDClient is based on this project, see this[INTRODUCTION OF SPRDCLIENT](https://www.bilibili.com/video/BV1dXTXzGEzY/).
## Features
###  Brom/Fdl1/Fdl2 Stage Features
-   Support kicking device to cali_diag/dl_diag(sprd4)
-   Support (re)connecting device in all stages(brom/fdl1/fdl2)
-   Support sending fdls to specified address and execute
-   Support reading/writing partitions in fdl2 stage
-   Support getting partition list in fdl2 stage
-   Support reseting device to normal/recovery/fastboot modes
###  Development Features
- Developing with c# .net
- Easy to develop your own SPRD(unisoc) flash tool with SPRDClientCore
---
## How to start
1. Clone this project to your own computer
2. Open the solution with **Visual Studio 2022** or newer
3. Install **System.IO.Ports** and **System.Management** libraries in **NuGet Package Manager**
4. Now you can start developing easily with SPRDClientCore
5. **Optional But Recommended:** Add all the usings as global usings in your project, just like 
```csharp
global using SPRDClientCore.Models;
global using SPRDClientCore.Utils;
global using SPRDClientCore.Protocol;
global using SPRDClientCore.Protocol.CheckSums;
global using SPRDClientCore.Protocol.Encoders;
global using static SPRDClientCore.Utils.SprdFlashUtils;
global using static SPRDClientCore.Models.SprdExceptions;
global using static SPRDClientCore.Models.SprdCommand;
```
6. **Optional:** You can build this project into a DLL so that you can import the libraries in other projects easily
## Examples of Developing
### Find device port:
You can easily find port by calling the static function `SprdProtocolHandler.FindComPort()`.
### Initialize Protocol Handler and Flash Utils

**SprdProtocolHandler** is a class which implement **IProtocolHandler** interface and can be used to communicate with unisoc(sprd) devices such as sending and receiving packets with sprd driver on Windows Platform. 
**SprdFlashUtils** is an important class with all the common functions like **ConnectToDevice**, **SendFile**, **WritePartition**, **ReadPartitionCustomize**, **Repartition** and so on.

Here is an example:
```csharp
string port = SprdProtocolHandler.FindComPort(timeout: 30000);
SprdProtocolHandler handler = new SprdProtocolHandler(port,new HdlcEncoder());
SprdFlashUtils utils = new SprdFlashUtils(handler);
```
### Connect to device and get the device stages:
"**Stages**" is an enum,including brom,fdl1,fdl2,sprd3 and sprd4. You can use `ConnectToDevice()` function to get the device stages. The function returns **(Stages SprdMode, Stages Stage)**.

Here is an example:
```csharp
var stages = utils.ConnectToDevice();
Stages deviceSprdMode = stages.SprdMode;
Stages deviceStage = stages.Stage; 
```
