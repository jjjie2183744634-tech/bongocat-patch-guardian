using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class BongoCatAdaptivePatcher
{
    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        var stack = new Stack<TypeDefinition>(module.Types.Reverse());
        while (stack.Count > 0)
        {
            TypeDefinition type = stack.Pop();
            yield return type;
            for (int i = type.NestedTypes.Count - 1; i >= 0; --i)
                stack.Push(type.NestedTypes[i]);
        }
    }

    private static string Sha256(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    private static bool Calls(Instruction instruction, string typeName, string methodName)
    {
        var method = instruction.Operand as MethodReference;
        return method != null && method.DeclaringType.FullName == typeName && method.Name == methodName;
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: BongoCatAdaptivePatcher <unmodified Assembly-CSharp.dll> <output DLL>");
                return 2;
            }

            string input = Path.GetFullPath(args[0]);
            string output = Path.GetFullPath(args[1]);
            string inputHash = Sha256(input);
            Guid inputMvid;

            using (var module = ModuleDefinition.ReadModule(input, new ReaderParameters { InMemory = true, ReadSymbols = false }))
            {
                inputMvid = module.Mvid;

                TypeDefinition shop = module.Types.Single(t => t.FullName == "BongoCat.Shop");
                MethodDefinition onChestReady = shop.Methods.Single(m => m.Name == "OnChestReady" && !m.HasParameters);
                MethodDefinition onClick = shop.Methods.Single(m => m.Name == "OnClick" && !m.HasParameters);
                MethodDefinition itemGotBought = shop.Methods.Single(m => m.Name == "ItemGotBought" && !m.HasParameters);
                FieldDefinition isEmoteShop = shop.Fields.Single(f => f.Name == "_isEmoteShop");
                FieldDefinition chestIsReady = shop.Fields.Single(f => f.Name == "ChestIsReady");
                FieldDefinition normalShop = shop.Fields.Single(f => f.Name == "NormalShop");
                FieldDefinition emoteShop = shop.Fields.Single(f => f.Name == "EmoteShop");

                MethodReference actionCtor = itemGotBought.Body.Instructions
                    .Where(i => i.OpCode == OpCodes.Newobj)
                    .Select(i => i.Operand as MethodReference)
                    .Single(m => m != null && m.DeclaringType.FullName == "System.Action" && m.Name == ".ctor");
                MethodReference callDelayed = itemGotBought.Body.Instructions
                    .Where(i => i.OpCode == OpCodes.Call)
                    .Select(i => i.Operand as MethodReference)
                    .Single(m => m != null && m.DeclaringType.FullName == "IroxGames.Helper.RoutineHelper" && m.Name == "CallDelayed");
                MethodReference startCoroutine = itemGotBought.Body.Instructions
                    .Where(i => i.OpCode == OpCodes.Call)
                    .Select(i => i.Operand as MethodReference)
                    .Single(m => m != null && m.DeclaringType.FullName == "UnityEngine.MonoBehaviour" && m.Name == "StartCoroutine");
                // Normal cosmetic chest always has priority over the emote chest.
                ILProcessor clickIl = onClick.Body.GetILProcessor();
                Instruction originalClickStart = onClick.Body.Instructions[0];
                Instruction insufficientPointsRet = onClick.Body.Instructions
                    .Where(i => i.OpCode == OpCodes.Ret)
                    .Single(i => i.Previous != null && Calls(i.Previous, "FlashAnimation", "Flash"));
                Instruction[] clickPriority =
                {
                    clickIl.Create(OpCodes.Ldarg_0),
                    clickIl.Create(OpCodes.Ldfld, isEmoteShop),
                    clickIl.Create(OpCodes.Brfalse, originalClickStart),
                    clickIl.Create(OpCodes.Ldsfld, normalShop),
                    clickIl.Create(OpCodes.Brfalse, originalClickStart),
                    clickIl.Create(OpCodes.Ldsfld, normalShop),
                    clickIl.Create(OpCodes.Ldfld, chestIsReady),
                    clickIl.Create(OpCodes.Brfalse, originalClickStart),
                    clickIl.Create(OpCodes.Ret)
                };
                foreach (Instruction instruction in clickPriority)
                    clickIl.InsertBefore(originalClickStart, instruction);

                // If a claim still lacks points, retry it after 60 seconds.
                Instruction[] clickRetry =
                {
                    clickIl.Create(OpCodes.Ldarg_0),
                    clickIl.Create(OpCodes.Ldarg_0),
                    clickIl.Create(OpCodes.Ldftn, onClick),
                    clickIl.Create(OpCodes.Newobj, actionCtor),
                    clickIl.Create(OpCodes.Ldc_R4, 60f),
                    clickIl.Create(OpCodes.Call, callDelayed),
                    clickIl.Create(OpCodes.Call, startCoroutine),
                    clickIl.Create(OpCodes.Pop)
                };
                foreach (Instruction instruction in clickRetry)
                    clickIl.InsertBefore(insufficientPointsRet, instruction);

                // Claim a ready chest after one second; emotes wait for a ready normal chest.
                ILProcessor readyIl = onChestReady.Body.GetILProcessor();
                Instruction finalReadyRet = onChestReady.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
                Instruction scheduleReadyClick = readyIl.Create(OpCodes.Ldarg_0);
                Instruction[] readyInjection =
                {
                    readyIl.Create(OpCodes.Ldarg_0),
                    readyIl.Create(OpCodes.Ldfld, isEmoteShop),
                    readyIl.Create(OpCodes.Brfalse, scheduleReadyClick),
                    readyIl.Create(OpCodes.Ldsfld, normalShop),
                    readyIl.Create(OpCodes.Brfalse, scheduleReadyClick),
                    readyIl.Create(OpCodes.Ldsfld, normalShop),
                    readyIl.Create(OpCodes.Ldfld, chestIsReady),
                    readyIl.Create(OpCodes.Brtrue, finalReadyRet),
                    scheduleReadyClick,
                    readyIl.Create(OpCodes.Ldarg_0),
                    readyIl.Create(OpCodes.Ldftn, onClick),
                    readyIl.Create(OpCodes.Newobj, actionCtor),
                    readyIl.Create(OpCodes.Ldc_R4, 1f),
                    readyIl.Create(OpCodes.Call, callDelayed),
                    readyIl.Create(OpCodes.Call, startCoroutine),
                    readyIl.Create(OpCodes.Pop)
                };
                foreach (Instruction instruction in readyInjection)
                    readyIl.InsertBefore(finalReadyRet, instruction);

                // One second after normal success, release an already-ready emote chest.
                ILProcessor boughtIl = itemGotBought.Body.GetILProcessor();
                Instruction finalBoughtRet = itemGotBought.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
                Instruction[] boughtInjection =
                {
                    boughtIl.Create(OpCodes.Ldarg_0),
                    boughtIl.Create(OpCodes.Ldfld, isEmoteShop),
                    boughtIl.Create(OpCodes.Brtrue, finalBoughtRet),
                    boughtIl.Create(OpCodes.Ldsfld, emoteShop),
                    boughtIl.Create(OpCodes.Brfalse, finalBoughtRet),
                    boughtIl.Create(OpCodes.Ldsfld, emoteShop),
                    boughtIl.Create(OpCodes.Ldfld, chestIsReady),
                    boughtIl.Create(OpCodes.Brfalse, finalBoughtRet),
                    boughtIl.Create(OpCodes.Ldsfld, emoteShop),
                    boughtIl.Create(OpCodes.Ldsfld, emoteShop),
                    boughtIl.Create(OpCodes.Ldftn, onClick),
                    boughtIl.Create(OpCodes.Newobj, actionCtor),
                    boughtIl.Create(OpCodes.Ldc_R4, 1f),
                    boughtIl.Create(OpCodes.Call, callDelayed),
                    boughtIl.Create(OpCodes.Call, startCoroutine),
                    boughtIl.Create(OpCodes.Pop)
                };
                foreach (Instruction instruction in boughtInjection)
                    boughtIl.InsertBefore(finalBoughtRet, instruction);

                // Add points through the game's real Cat.Tap path, isolated from GlobalKeyHook.
                TypeDefinition pets = module.Types.Single(t => t.FullName == "BongoCat.Pets");
                if (pets.Methods.Any(m => m.Name == "Update" && !m.HasParameters))
                    throw new InvalidOperationException("Pets already has Update; refusing ambiguous patch.");
                FieldDefinition petsInit = pets.Fields.Single(f => f.Name == "_init");
                MethodDefinition getCurrent = pets.Methods.Single(m => m.Name == "get_Current" && !m.HasParameters);
                TypeDefinition cat = module.Types.Single(t => t.FullName == "BongoCat.Cat");
                FieldDefinition catInstance = cat.Fields.Single(f => f.Name == "Instance");
                MethodDefinition catTap = cat.Methods.Single(m => m.Name == "Tap" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Int32");
                MethodDefinition autoTapUpdate = new MethodDefinition(
                    "Update", MethodAttributes.Private | MethodAttributes.HideBySig, module.TypeSystem.Void);
                pets.Methods.Add(autoTapUpdate);
                ILProcessor autoTapIl = autoTapUpdate.Body.GetILProcessor();
                Instruction autoTapRet = autoTapIl.Create(OpCodes.Ret);
                autoTapIl.Append(autoTapIl.Create(OpCodes.Ldarg_0));
                autoTapIl.Append(autoTapIl.Create(OpCodes.Ldfld, petsInit));
                autoTapIl.Append(autoTapIl.Create(OpCodes.Brfalse, autoTapRet));
                autoTapIl.Append(autoTapIl.Create(OpCodes.Ldarg_0));
                autoTapIl.Append(autoTapIl.Create(OpCodes.Call, getCurrent));
                autoTapIl.Append(autoTapIl.Create(OpCodes.Ldc_I4, 1000));
                autoTapIl.Append(autoTapIl.Create(OpCodes.Bge, autoTapRet));
                autoTapIl.Append(autoTapIl.Create(OpCodes.Ldsfld, catInstance));
                autoTapIl.Append(autoTapIl.Create(OpCodes.Brfalse, autoTapRet));
                autoTapIl.Append(autoTapIl.Create(OpCodes.Ldsfld, catInstance));
                autoTapIl.Append(autoTapIl.Create(OpCodes.Ldc_I4_1));
                autoTapIl.Append(autoTapIl.Create(OpCodes.Call, catTap));
                autoTapIl.Append(autoTapRet);

                // The update-resistant guardian starts the independent tray
                // helper. Keeping this out of the game assembly makes the
                // package independent of scheduled-task names and privileges.
                MethodDefinition petsAwake = pets.Methods.Single(m => m.Name == "Awake" && !m.HasParameters);

                // Record only real speech messages (channel 3), before bubble filtering/display.
                var loggerAssembly = new AssemblyNameReference("BongoCatChatLogger", new Version(1, 0, 0, 0));
                module.AssemblyReferences.Add(loggerAssembly);
                var loggerType = new TypeReference("", "BongoCatChatLogger", module, loggerAssembly);
                var loggerMethod = new MethodReference("Log", module.TypeSystem.Void, loggerType) { HasThis = false };
                loggerMethod.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                loggerMethod.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

                TypeDefinition multiplayer = module.Types.Single(t => t.FullName == "BongoCat.Multiplayer.SteamMultiplayer");
                TypeDefinition lobbyMessage = multiplayer.NestedTypes.Single(t => t.Name == "LobbyMessage");
                FieldDefinition channelField = lobbyMessage.Fields.Single(f => f.Name == "Channel");
                FieldDefinition messageField = lobbyMessage.Fields.Single(f => f.Name == "Message");
                MethodDefinition receive = multiplayer.Methods.Single(m => m.Name == "OnLobbyChatMsg" && m.Parameters.Count == 1);
                FieldReference senderField = receive.Body.Instructions.Select(i => i.Operand as FieldReference)
                    .Where(f => f != null)
                    .First(f => f.DeclaringType.FullName == "Heathen.SteamworksIntegration.LobbyChatMsg" && f.Name == "sender");
                MethodReference getName = AllTypes(module).SelectMany(t => t.Methods).Where(m => m.HasBody)
                    .SelectMany(m => m.Body.Instructions).Select(i => i.Operand as MethodReference).Where(m => m != null)
                    .First(m => m.DeclaringType.FullName == "Heathen.SteamworksIntegration.UserData" && m.Name == "get_Name");
                Instruction initialMessageLoad = receive.Body.Instructions.Single(i =>
                    i.OpCode == OpCodes.Ldloc_0 && i.Next != null && i.Next.OpCode == OpCodes.Ldfld &&
                    ((FieldReference)i.Next.Operand).FullName == channelField.FullName);
                Instruction originalChannelRead = initialMessageLoad.Next;
                ILProcessor receiveIl = receive.Body.GetILProcessor();
                Instruction[] logInjection =
                {
                    receiveIl.Create(OpCodes.Dup),
                    receiveIl.Create(OpCodes.Ldfld, channelField),
                    receiveIl.Create(OpCodes.Ldc_I4_3),
                    receiveIl.Create(OpCodes.Bne_Un_S, originalChannelRead),
                    receiveIl.Create(OpCodes.Ldarga_S, receive.Parameters[0]),
                    receiveIl.Create(OpCodes.Ldflda, senderField),
                    receiveIl.Create(OpCodes.Call, getName),
                    receiveIl.Create(OpCodes.Ldloc_0),
                    receiveIl.Create(OpCodes.Ldfld, messageField),
                    receiveIl.Create(OpCodes.Call, loggerMethod)
                };
                foreach (Instruction instruction in logInjection)
                    receiveIl.InsertBefore(originalChannelRead, instruction);

                // Poll the tray window's file outbox from the main cat. The
                // game's own SendSpeech method remains responsible for Steam,
                // language filtering, and the local speech bubble.
                var takeOutgoing = new MethodReference("TryTakeOutgoing", module.TypeSystem.String, loggerType)
                {
                    HasThis = false
                };
                TypeDefinition catSpeech = module.Types.Single(t => t.FullName == "Vfx.CatSpeech");
                FieldDefinition isMainCat = catSpeech.Fields.Single(f => f.Name == "_isMainCat");
                MethodDefinition speechUpdate = catSpeech.Methods.Single(m => m.Name == "Update" && !m.HasParameters);
                MethodDefinition sendSpeech = catSpeech.Methods.Single(m =>
                    m.Name == "SendSpeech" && m.Parameters.Count == 1 &&
                    m.Parameters[0].ParameterType.FullName == module.TypeSystem.String.FullName);
                FieldReference currentLobby = speechUpdate.Body.Instructions.Select(i => i.Operand as FieldReference)
                    .Where(f => f != null)
                    .First(f => f.DeclaringType.FullName == "Heathen.SteamworksIntegration.LobbyData" && f.Name == "Current");
                MethodReference getLobbyValid = speechUpdate.Body.Instructions.Select(i => i.Operand as MethodReference)
                    .Where(m => m != null)
                    .First(m => m.DeclaringType.FullName == "Heathen.SteamworksIntegration.LobbyData" && m.Name == "get_IsValid");
                var outgoingLocal = new VariableDefinition(module.TypeSystem.String);
                speechUpdate.Body.Variables.Add(outgoingLocal);
                speechUpdate.Body.InitLocals = true;
                Instruction originalSpeechUpdateStart = speechUpdate.Body.Instructions[0];
                ILProcessor speechUpdateIl = speechUpdate.Body.GetILProcessor();
                Instruction[] sendInjection =
                {
                    speechUpdateIl.Create(OpCodes.Ldarg_0),
                    speechUpdateIl.Create(OpCodes.Ldfld, isMainCat),
                    speechUpdateIl.Create(OpCodes.Brfalse, originalSpeechUpdateStart),
                    speechUpdateIl.Create(OpCodes.Ldsflda, currentLobby),
                    speechUpdateIl.Create(OpCodes.Call, getLobbyValid),
                    speechUpdateIl.Create(OpCodes.Brfalse, originalSpeechUpdateStart),
                    speechUpdateIl.Create(OpCodes.Call, takeOutgoing),
                    speechUpdateIl.Create(OpCodes.Stloc, outgoingLocal),
                    speechUpdateIl.Create(OpCodes.Ldloc, outgoingLocal),
                    speechUpdateIl.Create(OpCodes.Brfalse, originalSpeechUpdateStart),
                    speechUpdateIl.Create(OpCodes.Ldarg_0),
                    speechUpdateIl.Create(OpCodes.Ldloc, outgoingLocal),
                    speechUpdateIl.Create(OpCodes.Call, sendSpeech)
                };
                foreach (Instruction instruction in sendInjection)
                    speechUpdateIl.InsertBefore(originalSpeechUpdateStart, instruction);

                module.Write(output, new WriterParameters { WriteSymbols = false });
            }

            Verify(output, inputMvid);
            Console.WriteLine("Input SHA256 : " + inputHash);
            Console.WriteLine("Output SHA256: " + Sha256(output));
            Console.WriteLine("Compatible build: auto-claim, isolated 1000 floor, speech history, and tray outbox send verified.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message);
            return 1;
        }
    }

    private static void Verify(string output, Guid expectedMvid)
    {
        using (var module = ModuleDefinition.ReadModule(output, new ReaderParameters { ReadSymbols = false }))
        {
            TypeDefinition shop = module.Types.Single(t => t.FullName == "BongoCat.Shop");
            MethodDefinition onChestReady = shop.Methods.Single(m => m.Name == "OnChestReady" && !m.HasParameters);
            MethodDefinition onClick = shop.Methods.Single(m => m.Name == "OnClick" && !m.HasParameters);
            MethodDefinition itemGotBought = shop.Methods.Single(m => m.Name == "ItemGotBought" && !m.HasParameters);
            MethodDefinition timer = shop.NestedTypes.Single(t => t.Name.StartsWith("<TimerUpdate>d__", StringComparison.Ordinal))
                .Methods.Single(m => m.Name == "MoveNext");
            TypeDefinition pets = module.Types.Single(t => t.FullName == "BongoCat.Pets");
            MethodDefinition petsAwake = pets.Methods.Single(m => m.Name == "Awake" && !m.HasParameters);
            MethodDefinition petsUpdate = pets.Methods.Single(m => m.Name == "Update" && !m.HasParameters);
            MethodDefinition keyUpdate = module.Types.Single(t => t.FullName == "BongoCat.OSSpecific.GlobalKeyHook")
                .Methods.Single(m => m.Name == "Update" && !m.HasParameters);
            MethodDefinition receive = module.Types.Single(t => t.FullName == "BongoCat.Multiplayer.SteamMultiplayer")
                .Methods.Single(m => m.Name == "OnLobbyChatMsg" && m.Parameters.Count == 1);
            MethodDefinition speechUpdate = module.Types.Single(t => t.FullName == "Vfx.CatSpeech")
                .Methods.Single(m => m.Name == "Update" && !m.HasParameters);

            int readyDelayed = onChestReady.Body.Instructions.Count(i => Calls(i, "IroxGames.Helper.RoutineHelper", "CallDelayed"));
            int clickDelayed = onClick.Body.Instructions.Count(i => Calls(i, "IroxGames.Helper.RoutineHelper", "CallDelayed"));
            int boughtDelayed = itemGotBought.Body.Instructions.Count(i => Calls(i, "IroxGames.Helper.RoutineHelper", "CallDelayed"));
            int oneSecond = onChestReady.Body.Instructions.Concat(itemGotBought.Body.Instructions)
                .Count(i => i.OpCode == OpCodes.Ldc_R4 && i.Operand is float && Math.Abs((float)i.Operand - 1f) < 0.001f);
            int sixtySecond = onClick.Body.Instructions.Count(i =>
                i.OpCode == OpCodes.Ldc_R4 && i.Operand is float && Math.Abs((float)i.Operand - 60f) < 0.001f);
            int timerBuys = timer.Body.Instructions.Count(i => Calls(i, "BongoCat.ShopItem", "Buy"));
            int currentReads = petsUpdate.Body.Instructions.Count(i => Calls(i, "BongoCat.Pets", "get_Current"));
            int floorConstants = petsUpdate.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == 1000);
            int catTaps = petsUpdate.Body.Instructions.Count(i => Calls(i, "BongoCat.Cat", "Tap"));
            int keyWrites = keyUpdate.Body.Instructions.Count(i => i.OpCode == OpCodes.Stfld &&
                ((FieldReference)i.Operand).DeclaringType.FullName == "BongoCat.OSSpecific.GlobalKeyHook" &&
                ((FieldReference)i.Operand).Name == "_keysDown");
            int keyCurrentReads = keyUpdate.Body.Instructions.Count(i => Calls(i, "BongoCat.Pets", "get_Current"));
            int loggerRefs = module.AssemblyReferences.Count(a => a.Name == "BongoCatChatLogger" && a.Version == new Version(1, 0, 0, 0));
            int loggerCalls = receive.Body.Instructions.Count(i => Calls(i, "BongoCatChatLogger", "Log"));
            int outboxCalls = speechUpdate.Body.Instructions.Count(i => Calls(i, "BongoCatChatLogger", "TryTakeOutgoing"));
            int sendSpeechCalls = speechUpdate.Body.Instructions.Count(i => Calls(i, "Vfx.CatSpeech", "SendSpeech"));
            int hookRefs = module.AssemblyReferences.Count(a => a.Name == "BongoCatWindowHook");

            if (module.Mvid != expectedMvid || readyDelayed != 1 || clickDelayed != 1 || boughtDelayed != 2 ||
                oneSecond < 2 || sixtySecond != 1 || timerBuys != 0 || currentReads != 1 || floorConstants != 1 ||
                catTaps != 1 || keyWrites != 1 || keyCurrentReads != 0 || loggerRefs != 1 ||
                loggerCalls != 1 || outboxCalls != 1 ||
                sendSpeechCalls != 1 || hookRefs != 0)
                throw new InvalidOperationException("Post-write verification failed.");
        }
    }
}
