using ExDuiR.NET.Frameworks;
using ExDuiR.NET.Frameworks.Controls;
using ExDuiR.NET.Frameworks.Utility;
using ExDuiR.NET.Native;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TextCopy;
using static ExDuiR.NET.Native.ExConst;


namespace AgentFlow
{
    static class MainWindow
    {
        static private ExApp theApp;
        static private ExSkin skin;

        static private ExFlowGraph flowGraph;
        // 事件回调委托
        static private ExObjEventProcDelegate flowGraphNotifyProc;
        static private ExWndProcDelegate wndProcDelegate;
        static private int maxOutputTokens = 131072;
        // 节点ID
        static int g_nodeIdLocalText;
        static int g_nodeIdLLMText;
        static int g_nodeIdTextRender;

        static IntPtr g_llmComboData = IntPtr.Zero;
        static IntPtr g_btnCopy = IntPtr.Zero;

        // 组合框选项数组指针（非托管内存），用于清理
        static IntPtr g_llmOptionsPtr = IntPtr.Zero;
        static int g_llmOptionsCount = 0;

        static AIAgent agent;
        static string ollamaHost = "http://localhost:11434";
        static string ollamaModel = "QWEN3.8-Q6:latest";
        static string instructions = "你是一个助手";

        static public void CreateMainWindow()
        {
            var themeBytes = File.ReadAllBytes(Path.Combine(Environment.CurrentDirectory, "Default.bin"));
            theApp = new ExApp(themeBytes, ENGINE_FLAG_DPI_ENABLE | ENGINE_FLAG_MENU_ALL);
            wndProcDelegate = new ExWndProcDelegate(OnFlowGraphWndMsgProc);
            skin = new ExSkin(null, null,
                "AgentFlow演示",
                0, 0, 1400, 1000,
                WINDOW_STYLE_MAINWINDOW | WINDOW_STYLE_BUTTON_CLOSE | WINDOW_STYLE_BUTTON_MIN |
                WINDOW_STYLE_BUTTON_MAX | WINDOW_STYLE_MOVEABLE | WINDOW_STYLE_CENTERWINDOW |
                WINDOW_STYLE_TITLE | WINDOW_STYLE_SIZEABLE | WINDOW_STYLE_HASICON,
                0, 0, IntPtr.Zero, wndProcDelegate);
            skin.BackgroundColor = Util.ExARGB(80, 80, 90, 255);
            // 创建节点图控件
            flowGraph = new ExFlowGraph(skin, "", 10, 30, 1380, 900, -1, -1, -1, 200);
            RegisterBuiltinCardTypes();
            // 创建所有节点 + 添加连线 + 注册事件
            CreateNodes();
            AddConnections();
            RegisterAllEvents();

            CreatrAIAgent();

            //var ptr = StringArrayToPtr(new List<string> { "自定义条目1", "自定义条目2" });
            //ExFlowGraphCustomItems customItems = new ExFlowGraphCustomItems
            //{
            //    count = 2,
            //    items = ptr
            //};
            //flowGraph.SetCustomItems(customItems);
            //FreeStringArrayPtr(ptr, 2);
            skin.Visible = true;
            // 引擎消息循环
            theApp.Run();
        }

        static private void CreatrAIAgent()
        {
            agent = CreateOllamaAgent("agent", ollamaHost, ollamaModel, instructions, null);
        }

        static private void RegisterBuiltinCardTypes()
        {
            // 1. 初始化公共组件数据（非托管内存）
            var llmStrs = new List<string> { ollamaModel };
            g_llmOptionsPtr = StringArrayToPtr(llmStrs);
            g_llmOptionsCount = llmStrs.Count;
            g_llmComboData = ExAPI.Ex_MemAlloc(Marshal.SizeOf<ExFlowGraphNodeComboData>());
            var llmCombo = new ExFlowGraphNodeComboData { options = g_llmOptionsPtr, count = llmStrs.Count, current = 0 };
            Marshal.StructureToPtr(llmCombo, g_llmComboData, false);

            g_btnCopy = AllocStruct(new ExFlowGraphNodeButtonData { caption = Util.StrDupW("复制文本到剪贴板") });
    
            // 2. 注册所有卡片类型
            RegisterLocalTextCard();
            RegisterLLMTextCard();
            RegisterTextRenderCard();

        }
        static private IntPtr AllocStruct<T>(T data) where T : struct
        {
            IntPtr ptr = ExAPI.Ex_MemAlloc(Marshal.SizeOf<T>());
            Marshal.StructureToPtr(data, ptr, false);
            return ptr;
        }
        #region 辅助方法
        static private IntPtr StringArrayToPtr(List<string> vec)
        {
            IntPtr arr = ExAPI.Ex_MemAlloc(IntPtr.Size * vec.Count);
            for (int i = 0; i < vec.Count; i++)
            {
                Marshal.WriteIntPtr(arr, i * IntPtr.Size, Util.StrDupW(vec[i]));
            }
            return arr;
        }
        static private void FreeStringArrayPtr(IntPtr ptr, int count)
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr strPtr = Marshal.ReadIntPtr(ptr, i * IntPtr.Size);
                ExAPI.Ex_MemFree(strPtr);
            }
            ExAPI.Ex_MemFree(ptr);
        }
        static private void FreeCardRegistrationData(IntPtr pPorts, int portCount, IntPtr typeName, IntPtr dynamicPortNamePrefix = default, IntPtr dynamicPort2NamePrefix = default)
        {
            int portSize = Marshal.SizeOf<ExFlowGraphPortDesc>();
            for (int i = 0; i < portCount; i++)
            {
                ExFlowGraphPortDesc port = Marshal.PtrToStructure<ExFlowGraphPortDesc>(IntPtr.Add(pPorts, i * portSize));
                if (port.name != IntPtr.Zero) ExAPI.Ex_MemFree(port.name);
                if (port.defaultWidgetData != IntPtr.Zero &&
                    (port.widgetType == FLOWGRAPH_NODEDATA_TYPE_EDIT || port.widgetType == FLOWGRAPH_NODEDATA_TYPE_TEXT))
                {
                    ExAPI.Ex_MemFree(port.defaultWidgetData);
                }
            }
            ExAPI.Ex_MemFree(pPorts);
            if (typeName != IntPtr.Zero) ExAPI.Ex_MemFree(typeName);
            if (dynamicPortNamePrefix != IntPtr.Zero) ExAPI.Ex_MemFree(dynamicPortNamePrefix);
            if (dynamicPort2NamePrefix != IntPtr.Zero) ExAPI.Ex_MemFree(dynamicPort2NamePrefix);
        }
        static private void FreeComboData(ref IntPtr comboDataPtr, ref IntPtr optionsPtr, int count)
        {
            if (optionsPtr != IntPtr.Zero)
            {
                FreeStringArrayPtr(optionsPtr, count);
                optionsPtr = IntPtr.Zero;
            }
            if (comboDataPtr != IntPtr.Zero)
            {
                ExAPI.Ex_MemFree(comboDataPtr);
                comboDataPtr = IntPtr.Zero;
            }
        }
        static private void FreeButtonData(ref IntPtr btnDataPtr)
        {
            if (btnDataPtr != IntPtr.Zero)
            {
                var btn = Marshal.PtrToStructure<ExFlowGraphNodeButtonData>(btnDataPtr);
                if (btn.caption != IntPtr.Zero) ExAPI.Ex_MemFree(btn.caption);
                ExAPI.Ex_MemFree(btnDataPtr);
                btnDataPtr = IntPtr.Zero;
            }
        }

        static private void Cleanup()
        {
            FreeComboData(ref g_llmComboData, ref g_llmOptionsPtr, g_llmOptionsCount);
            FreeButtonData(ref g_btnCopy);
        }
        // ★ 新增：调试打印IO数据
        static private void DebugPrintIOData(string tag, IntPtr arr, int count, IntPtr pNode)
        {
            int ioSize = Marshal.SizeOf<ExFlowGraphNodeIoData>();
            int portSize = Marshal.SizeOf<ExFlowGraphPort>();

            // ★ 预取节点端口信息以判断 widgetType
            Dictionary<int, int> portWidgetTypes = new Dictionary<int, int>();
            if (pNode != IntPtr.Zero)
            {
                var node = Marshal.PtrToStructure<ExFlowGraphNode>(pNode);
                for (int j = 0; j < node.portCount; j++)
                {
                    var p = Marshal.PtrToStructure<ExFlowGraphPort>(IntPtr.Add(node.ports, j * portSize));
                    portWidgetTypes[p.id] = p.widgetType;
                }
            }

            for (int i = 0; i < count; i++)
            {
                var io = Marshal.PtrToStructure<ExFlowGraphNodeIoData>(IntPtr.Add(arr, i * ioSize));

                int widgetType = 0;
                portWidgetTypes.TryGetValue(io.portId, out widgetType);

                switch (io.dataType)
                {
                    case FLOWGRAPH_DATATYPE_STRING:
                        // ★ 修复：如果是按钮组件，data 是结构体指针，需解析 caption
                        if (widgetType == FLOWGRAPH_NODEDATA_TYPE_BUTTON)
                        {
                            var btn = Marshal.PtrToStructure<ExFlowGraphNodeButtonData>(io.data);
                            string caption = btn.caption != IntPtr.Zero ? Marshal.PtrToStringUni(btn.caption) : "(null)";
                            Console.WriteLine($"{tag}  [{i}] portId={io.portId}, type=STRING(BUTTON), caption=\"{caption}\"");
                        }
                        else if (widgetType == FLOWGRAPH_NODEDATA_TYPE_DUAL_BUTTON)
                        {
                            var dual = Marshal.PtrToStructure<ExFlowGraphNodeDualButtonData>(io.data);
                            string cap1 = dual.caption1 != IntPtr.Zero ? Marshal.PtrToStringUni(dual.caption1) : "(null)";
                            string cap2 = dual.caption2 != IntPtr.Zero ? Marshal.PtrToStringUni(dual.caption2) : "(null)";
                            Console.WriteLine($"{tag}  [{i}] portId={io.portId}, type=STRING(DUAL_BTN), cap1=\"{cap1}\", cap2=\"{cap2}\"");
                        }
                        else
                        {
                            string str = io.data != IntPtr.Zero ? Marshal.PtrToStringUni(io.data) ?? "(null)" : "(null)";
                            Console.WriteLine($"{tag}  [{i}] portId={io.portId}, type=STRING, data=\"{str}\"");
                        }
                        break;
                    case FLOWGRAPH_DATATYPE_IMAGE:
                        Console.WriteLine($"{tag}  [{i}] portId={io.portId}, type=IMAGE, data=0x{io.data.ToString("X")}");
                        break;
                    case FLOWGRAPH_DATATYPE_COMBO:
                        if (io.data != IntPtr.Zero)
                        {
                            var combo = Marshal.PtrToStructure<ExFlowGraphNodeComboData>(io.data);
                            string currentVal = "(null)";
                            if (combo.count > 0 && combo.options != IntPtr.Zero)
                            {
                                IntPtr selPtr = Marshal.ReadIntPtr(combo.options, combo.current * IntPtr.Size);
                                currentVal = Marshal.PtrToStringUni(selPtr) ?? "(null)";
                            }
                            Console.WriteLine($"{tag}  [{i}] portId={io.portId}, type=COMBO, count={combo.count}, current={combo.current}, value=\"{currentVal}\"");
                        }
                        else
                        {
                            Console.WriteLine($"{tag}  [{i}] portId={io.portId}, type=COMBO, data=(null)");
                        }
                        break;
                    case FLOWGRAPH_DATATYPE_ANY:
                        Console.WriteLine($"{tag}  [{i}] portId={io.portId}, type=ANY, data=0x{io.data.ToString("X")}");
                        break;
                    case FLOWGRAPH_DATATYPE_VIDEO:
                        Console.WriteLine($"{tag}  [{i}] portId={io.portId}, type=VIDEO, data=0x{io.data.ToString("X")}");
                        break;
                    default:
                        Console.WriteLine($"{tag}  [{i}] portId={io.portId}, type=UNKNOWN({io.dataType}), data=0x{io.data.ToString("X")}");
                        break;
                }
            }
        }
        #endregion
        #region 创建节点
        static private void CreateNodes()
        {
            ExFlowGraphCustomNodeCreate localText = new ExFlowGraphCustomNodeCreate
            {
                x = 100,
                y = 100,
                title = Util.StrDupW("本地文本节点"),
                cardType = FLOWGRAPH_CARD_TYPE_LOCAL_TEXT
            };
            g_nodeIdLocalText = flowGraph.CreateCustomNode(localText);
            ExAPI.Ex_MemFree(localText.title);
            ExFlowGraphCustomNodeCreate llmText = new ExFlowGraphCustomNodeCreate
            {
                x = 100,
                y = 400,
                title = Util.StrDupW("大模型文本节点"),
                cardType = FLOWGRAPH_CARD_TYPE_LLM_TEXT
            };
            g_nodeIdLLMText = (int)flowGraph.CreateCustomNode(llmText);
            ExAPI.Ex_MemFree(llmText.title);
            ExFlowGraphCustomNodeCreate textRender = new ExFlowGraphCustomNodeCreate
            {
                x = 500,
                y = 200,
                title = Util.StrDupW("文本显示节点"),
                cardType = FLOWGRAPH_CARD_TYPE_TEXT_RENDER
            };
            g_nodeIdTextRender = (int)flowGraph.CreateCustomNode(textRender);
            ExAPI.Ex_MemFree(textRender.title);

        }
        #endregion
        #region 添加连线
        static private void AddConnections()
        {
            ExFlowGraphConnection conn = new ExFlowGraphConnection();
      
            // 本地文本 → 大模型文本(输入文本端口)
            conn.fromNode = g_nodeIdLocalText; conn.fromSlot = 1; conn.toNode = g_nodeIdLLMText; conn.toSlot = 0;
            flowGraph.AddConnection(conn);
            // 大模型文本 → 文本显示节点
            conn.fromNode = g_nodeIdLLMText; conn.fromSlot = 3; conn.toNode = g_nodeIdTextRender; conn.toSlot = 0;
            flowGraph.AddConnection(conn);

        }
        #endregion
        #region 事件回调
        static public IntPtr OnFlowGraphNotifyProc(int hObj, int nID, int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nID != 200) return IntPtr.Zero;
            try
            {
                switch (nCode)
                {
                    case FLOWGRAPH_EVENT_EXECUTE_NODE:
                        {
                            var param = Marshal.PtrToStructure<ExFlowGraphExecuteParams>(lParam);
                            HandleNodeExecute(ref param, lParam);
                            break;
                        }
                    case FLOWGRAPH_EVENT_NODE_CANCELED:
                        {
                            int nodeId = (int)wParam;
                            int cardType = (int)lParam;
                            Console.WriteLine($"节点执行被取消 {nodeId} {cardType}");
                            break;
                        }
                    case FLOWGRAPH_EVENT_BUTTON_CLICKED:
                        {
                            var info = Marshal.PtrToStructure<ExFlowGraphButtonClickInfo>(lParam);
                            HandleButtonClick(info);
                            break;
                        }
                    case FLOWGRAPH_EVENT_NODEDATA_COMBO_CHANGED:
                        {
                            var port = Marshal.PtrToStructure<ExFlowGraphPort>(lParam);
                            var combo = Marshal.PtrToStructure<ExFlowGraphNodeComboData>(port.widgetData);
                            string text = Marshal.PtrToStringUni(Marshal.ReadIntPtr(combo.options, combo.current * IntPtr.Size));
                            Console.WriteLine($"[组合框改变] nodeId={wParam}, 选中={text}");
                            break;
                        }
                    case FLOWGRAPH_EVENT_SIDEBAR_ADD_CARD:
                        {
                            HandleSidebarAddCard((int)wParam);
                            break;
                        }
                    case FLOWGRAPH_EVENT_CUSTOM_ITEM_CLICKED:
                        {
                            int itemIndex = (int)wParam;
                            string itemName = Marshal.PtrToStringUni(lParam);
                            Console.WriteLine($"[自定义条目点击] index = {itemIndex}, name = {itemName}");
                            break;
                        }
                }
            }
            catch { }
            return IntPtr.Zero;
        }
        #endregion
        // ★★★ 处理节点执行（完整同步/异步逻辑 + 详细调试打印）★★★
        static private void HandleNodeExecute(ref ExFlowGraphExecuteParams param, IntPtr paramPtr)
        {
            param.executionResult = FLOWGRAPH_EXEC_RESULT_FAILED;
            int ioSize = Marshal.SizeOf<ExFlowGraphNodeIoData>();
            IntPtr pNode = flowGraph.FindNode(param.nodeId);
            // ===== 通用打印 =====
            Console.WriteLine("══════════════════════════════════════════");
            Console.WriteLine($"[执行事件] nodeId={param.nodeId}, cardType={param.cardType}");
            Console.WriteLine($"── 输入端口 (count={param.inputCount}) ──");
            DebugPrintIOData("[INPUT]", param.inputs, param.inputCount, pNode); // ★ 传入 pNode
            Console.WriteLine($"── 输出端口 (count={param.outputCount}) ──");
            DebugPrintIOData("[OUTPUT]", param.outputs, param.outputCount, pNode); // ★ 传入 pNode
            switch (param.cardType)
            {
                // ==================== 本地文本 ====================
                case FLOWGRAPH_CARD_TYPE_LOCAL_TEXT:
                    {
                        // ★ 端口布局: [0]INTERMEDIATE/TEXT("内容") [1]OUTPUT/STRING("输出文本")
                        Console.WriteLine("[本地文本] 端口布局: [0]INTERMEDIATE/TEXT(内容) [1]OUTPUT/STRING(输出文本)");
                        var input = Marshal.PtrToStructure<ExFlowGraphNodeIoData>(IntPtr.Add(param.inputs, 0 * ioSize));
                        string text = input.data != IntPtr.Zero ? Marshal.PtrToStringUni(input.data) ?? "" : "";
                        Console.WriteLine($"[本地文本] inputs[0].data(内容)=\"{text}\"");
                        var output = Marshal.PtrToStructure<ExFlowGraphNodeIoData>(IntPtr.Add(param.outputs, 0 * ioSize));
                        output.data = input.data != IntPtr.Zero ? Util.StrDupW(text) : IntPtr.Zero;
                        Marshal.StructureToPtr(output, IntPtr.Add(param.outputs, 0 * ioSize), false);
                        Console.WriteLine($"[本地文本] outputs[0].data(输出)=\"{text}\"");
                        param.executionResult = FLOWGRAPH_EXEC_RESULT_SUCCESS;
                        break;
                    }
                // ==================== 大模型文本 ====================
                case FLOWGRAPH_CARD_TYPE_LLM_TEXT:
                    {
                        // ★ 端口布局: [0]INPUT/STRING(输入文本) [1]INTERMEDIATE/COMBO(模型) [2]INTERMEDIATE/TEXT(提示词) [3]OUTPUT/STRING(输出文本)
                        Console.WriteLine("[大模型文本] 端口布局: [0]INPUT/STRING(输入文本) [1]INTERMEDIATE/COMBO(模型) [2]INTERMEDIATE/TEXT(提示词) [3]OUTPUT/STRING(输出文本)");
                        var input0 = Marshal.PtrToStructure<ExFlowGraphNodeIoData>(IntPtr.Add(param.inputs, 0 * ioSize));
                        string inputText = Marshal.PtrToStringUni(input0.data) ?? "";
                        Console.WriteLine($"[大模型文本] inputs[0].data(输入文本)=\"{inputText}\"");
                        var input1 = Marshal.PtrToStructure<ExFlowGraphNodeIoData>(IntPtr.Add(param.inputs, 1 * ioSize));
                        string modelName = "";
                        if (input1.data != IntPtr.Zero)
                        {
                            var combo = Marshal.PtrToStructure<ExFlowGraphNodeComboData>(input1.data);
                            if (combo.count > 0 && combo.options != IntPtr.Zero)
                            {
                                IntPtr selectedPtr = Marshal.ReadIntPtr(combo.options, combo.current * IntPtr.Size);
                                modelName = Marshal.PtrToStringUni(selectedPtr) ?? "";
                            }
                            Console.WriteLine($"[大模型文本] inputs[1].data(模型) current={combo.current}, value=\"{modelName}\"");
                        }
                        else
                        {
                            Console.WriteLine("[大模型文本] inputs[1].data(模型)=(null)");
                        }
                        // ★ inputs[2] = INTERMEDIATE/TEXT("提示词")
                        var input2 = Marshal.PtrToStructure<ExFlowGraphNodeIoData>(IntPtr.Add(param.inputs, 2 * ioSize));
                        string promptText = Marshal.PtrToStringUni(input2.data) ?? "";
                        Console.WriteLine($"[大模型文本] inputs[2].data(提示词)=\"{promptText}\"");
                        string fullPrompt = inputText + "\n" + promptText;
                        Console.WriteLine($"[大模型文本] 拼接后完整提示=\"{fullPrompt}\"");
                        if (!(string.IsNullOrEmpty(promptText) && string.IsNullOrEmpty(inputText)))
                        {
                            flowGraph.MarkAsyncNode(param.nodeId);
                            var localParam = param;
                            
                            Task.Run(async () => await SimulateLLMAsync(localParam.nodeId, fullPrompt));
                            param.executionResult = FLOWGRAPH_EXEC_RESULT_SUCCESS;
                            Console.WriteLine("[大模型文本] 已标记异步执行，等待回调...");
                        }
                        else
                        {
                            Console.WriteLine("[大模型文本] 执行失败：完整提示为空");
                        }
                        break;
                    }
                // ==================== 文本显示 ====================
                case FLOWGRAPH_CARD_TYPE_TEXT_RENDER:
                    {
                        Console.WriteLine("[文本显示] 端口布局: [0]INPUT/STRING(输入文本) [1]INTERMEDIATE/TEXT(显示文本) [2]INTERMEDIATE/BUTTON(复制) [3]OUTPUT/STRING(输出文本)");
                        var input = Marshal.PtrToStructure<ExFlowGraphNodeIoData>(IntPtr.Add(param.inputs, 0 * ioSize));
                        string text = Marshal.PtrToStringUni(input.data) ?? "";
                        Console.WriteLine($"[文本显示] inputs[0].data(输入文本)=\"{text}\"");
                        if (pNode != IntPtr.Zero)
                        {
                            var node = Marshal.PtrToStructure<ExFlowGraphNode>(pNode);
                            int portSize = Marshal.SizeOf<ExFlowGraphPort>();
                            for (int i = 0; i < node.portCount; i++)
                            {
                                var port = Marshal.PtrToStructure<ExFlowGraphPort>(IntPtr.Add(node.ports, i * portSize));
                                if (port.portType == FLOWGRAPH_PORTTYPE_INTERMEDIATE && port.widgetType == FLOWGRAPH_NODEDATA_TYPE_TEXT)
                                {
                                    Console.WriteLine($"[文本显示] 找到TEXT端口 index={i}, portId={port.id}, name=\"{Marshal.PtrToStringUni(port.name)}\"");
                                    IntPtr textPtr = Util.StrDupW(text);
                                    var newPort = new ExFlowGraphPort { id = port.id, widgetType = FLOWGRAPH_NODEDATA_TYPE_TEXT, widgetData = textPtr };
                                    flowGraph.UpdateNodeData(param.nodeId, newPort);
                                    ExAPI.Ex_MemFree(textPtr);
                                    Console.WriteLine($"[文本显示] 已更新TEXT端口内容=\"{text}\"");
                                    break;
                                }
                            }
                        }
                        var output = Marshal.PtrToStructure<ExFlowGraphNodeIoData>(IntPtr.Add(param.outputs, 0 * ioSize));
                        output.data = Util.StrDupW(text);
                        Marshal.StructureToPtr(output, IntPtr.Add(param.outputs, 0 * ioSize), false);
                        Console.WriteLine($"[文本显示] outputs[0].data(输出)=\"{text}\"");
                        param.executionResult = FLOWGRAPH_EXEC_RESULT_SUCCESS;
                        break;
                    }
                default:
                    Console.WriteLine($"[未知卡片] cardType={param.cardType}, 设置执行失败");
                    break;
            }
            Console.WriteLine($"[执行事件] 返回结果 executionResult={param.executionResult}");
            Console.WriteLine("══════════════════════════════════════════");
            Marshal.StructureToPtr(param, paramPtr, false);
        }
        #region 异步模拟
        static private async Task SimulateLLMAsync(int nodeId, string prompt)
        {
            var session = await agent.CreateSessionAsync();
            var sb = new StringBuilder();
            int chunkNo = 0;
            await foreach (var chunk in agent.RunStreamingAsync(
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt), session))
            {
                chunkNo++;
                foreach (var c in chunk.Contents)
                {
                    switch (c)
                    {
                        case TextReasoningContent r when !string.IsNullOrEmpty(r.Text):
                            Console.ForegroundColor = ConsoleColor.Blue;
                            Console.Write(r.Text);
                            Console.ResetColor();
                            Console.Out.Flush();
                            break;
                        case TextContent t when !string.IsNullOrEmpty(t.Text):
                            Console.Write(t.Text);
                            Console.Out.Flush();
                            sb.Append(t.Text);
                            break;
                        case FunctionCallContent fc:
                            Console.ForegroundColor = ConsoleColor.DarkYellow;
                            Console.WriteLine($"\n[工具调用] {fc.Name}({AotJsonHelper.SafeSerializeArguments(fc.Arguments)})");
                            Console.ResetColor();
                            sb.AppendLine($"[调用工具] {fc.Name}({AotJsonHelper.SafeSerializeArguments(fc.Arguments)})");
                            break;
                        case FunctionResultContent fr:
                            var resultStr = fr.Result?.ToString() ?? "";
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine($"[工具结果] {Truncate(resultStr, 300)}");
                            Console.ResetColor();
                            sb.AppendLine($"[工具结果] {Truncate(resultStr, 500)}");
                            break;
                        case UsageContent:
                            break;
                    }
                }
            }
            var allStr = sb.ToString();
            var executionResult = FLOWGRAPH_EXEC_RESULT_SUCCESS;
            ExFlowGraphAsyncResult result = new ExFlowGraphAsyncResult
            {
                nodeId = nodeId,
                cardType = FLOWGRAPH_CARD_TYPE_LLM_TEXT,
                executionResult = executionResult,
                outputText = Util.StrDupW(allStr),
                outputImage = 0,
            };
            flowGraph.NodeExecutionComplete(result);
            ExAPI.Ex_MemFree(result.outputText);
        }


        static string? Truncate(string? s, int n) =>
    string.IsNullOrEmpty(s) ? s : (s.Length <= n ? s : s.Substring(0, n) + "...");


        #endregion
        #region 工具方法（按钮点击/侧边栏/窗口回调）
        static private void HandleButtonClick(ExFlowGraphButtonClickInfo info)
        {
            Console.WriteLine($"[按钮点击] nodeId={info.nodeId}, cardType={info.cardType}");
            
            if (info.cardType == FLOWGRAPH_CARD_TYPE_TEXT_RENDER)
            {
                IntPtr pNode = flowGraph.FindNode(info.nodeId);
                if (pNode == IntPtr.Zero) return;
                ExFlowGraphNode node = Marshal.PtrToStructure<ExFlowGraphNode>(pNode);
                int portSize = Marshal.SizeOf<ExFlowGraphPort>();
                for (int i = 0; i < node.portCount; i++)
                {
                    ExFlowGraphPort port = Marshal.PtrToStructure<ExFlowGraphPort>(IntPtr.Add(node.ports, i * portSize));
                    if (port.widgetType == FLOWGRAPH_NODEDATA_TYPE_TEXT && port.widgetData != IntPtr.Zero)
                    {
                        string text = Marshal.PtrToStringUni(port.widgetData);
                        ClipboardService.SetText(text);
                        break;
                    }
                }
            }
        }
        static private void HandleSidebarAddCard(int cardType)
        {
            string title = cardType switch
            {
                FLOWGRAPH_CARD_TYPE_LOCAL_TEXT => "本地文本节点",
                FLOWGRAPH_CARD_TYPE_LLM_TEXT => "大模型文本节点",
                FLOWGRAPH_CARD_TYPE_TEXT_RENDER => "文本显示节点",
                _ => "自定义节点"
            };
            var param = new ExFlowGraphCustomNodeCreate { x = FLOWGRAPH_AUTO_POSITION, y = FLOWGRAPH_AUTO_POSITION, title = Util.StrDupW(title), cardType = cardType };
            flowGraph.CreateCustomNode(param);
            ExAPI.Ex_MemFree(param.title);
        }
        static private IntPtr OnFlowGraphWndMsgProc(IntPtr hWnd, int hExDui, int uMsg, IntPtr wParam, IntPtr lParam, IntPtr pResult)
        {
            if (uMsg == WM_SIZE)
            {
                int width = (int)Util.LOWORD((uint)lParam);
                int height = (int)Util.HIWORD((uint)lParam);
                flowGraph.Move(0, (int)ExAPI.Ex_Scale(30), width, height - (int)ExAPI.Ex_Scale(50), false);
            }
            else if (uMsg == WM_DESTROY)
            {
                Cleanup();
            }
            return IntPtr.Zero;
        }
        static private void RegisterAllEvents()
        {
            flowGraphNotifyProc = new ExObjEventProcDelegate(OnFlowGraphNotifyProc);
            flowGraph.HandleEvent(FLOWGRAPH_EVENT_EXECUTE_NODE, flowGraphNotifyProc);
            flowGraph.HandleEvent(FLOWGRAPH_EVENT_BUTTON_CLICKED, flowGraphNotifyProc);
            flowGraph.HandleEvent(FLOWGRAPH_EVENT_NODEDATA_COMBO_CHANGED, flowGraphNotifyProc);
            flowGraph.HandleEvent(FLOWGRAPH_EVENT_SIDEBAR_ADD_CARD, flowGraphNotifyProc);
            flowGraph.HandleEvent(FLOWGRAPH_EVENT_CUSTOM_ITEM_CLICKED, flowGraphNotifyProc);
            flowGraph.HandleEvent(FLOWGRAPH_EVENT_NODE_CANCELED, flowGraphNotifyProc);
        }
        #endregion
        // ★ 注册：本地文本 (EDIT→TEXT)
        static private void RegisterLocalTextCard()
        {
            var ports = new ExFlowGraphPortDesc[2];
            ports[0] = new ExFlowGraphPortDesc { portType = FLOWGRAPH_PORTTYPE_INTERMEDIATE, dataType = FLOWGRAPH_DATATYPE_STRING, name = Util.StrDupW("内容"), widgetType = FLOWGRAPH_NODEDATA_TYPE_TEXT, widgetWidth = 300, defaultWidgetData = Util.StrDupW("") }; // ★ EDIT→TEXT
            ports[1] = new ExFlowGraphPortDesc { portType = FLOWGRAPH_PORTTYPE_OUTPUT, dataType = FLOWGRAPH_DATATYPE_STRING, name = Util.StrDupW("输出文本") };
            IntPtr pPorts = ExAPI.Ex_MemAlloc(Marshal.SizeOf<ExFlowGraphPortDesc>() * 2);
            for (int i = 0; i < 2; i++) Marshal.StructureToPtr(ports[i], IntPtr.Add(pPorts, i * Marshal.SizeOf<ExFlowGraphPortDesc>()), false);
            IntPtr typeName = Util.StrDupW("本地文本");
            var desc = new ExFlowGraphCardDescriptor
            {
                cardType = FLOWGRAPH_CARD_TYPE_LOCAL_TEXT,
                typeName = typeName,
                tagColor = Util.ExARGB(80, 130, 80, 255),
                portCount = 2,
                ports = pPorts
            };
            flowGraph.RegisterCardType(desc);
            FreeCardRegistrationData(pPorts, 2, typeName);
        }
        // ★ 注册：大模型文本 (EDIT→TEXT)
        static private void RegisterLLMTextCard()
        {
            var ports = new ExFlowGraphPortDesc[4];
            ports[0] = new ExFlowGraphPortDesc { portType = FLOWGRAPH_PORTTYPE_INPUT, dataType = FLOWGRAPH_DATATYPE_STRING, name = Util.StrDupW("输入文本") };
            ports[1] = new ExFlowGraphPortDesc { portType = FLOWGRAPH_PORTTYPE_INTERMEDIATE, dataType = FLOWGRAPH_DATATYPE_COMBO, name = Util.StrDupW("模型"), widgetType = FLOWGRAPH_NODEDATA_TYPE_COMBO, widgetWidth = 300, widgetHeight = 30, defaultWidgetData = g_llmComboData };
            ports[2] = new ExFlowGraphPortDesc { portType = FLOWGRAPH_PORTTYPE_INTERMEDIATE, dataType = FLOWGRAPH_DATATYPE_STRING, name = Util.StrDupW("提示词"), widgetType = FLOWGRAPH_NODEDATA_TYPE_TEXT, widgetWidth = 500, defaultWidgetData = Util.StrDupW("") }; // ★ EDIT→TEXT
            ports[3] = new ExFlowGraphPortDesc { portType = FLOWGRAPH_PORTTYPE_OUTPUT, dataType = FLOWGRAPH_DATATYPE_STRING, name = Util.StrDupW("输出文本") };
            IntPtr pPorts = ExAPI.Ex_MemAlloc(Marshal.SizeOf<ExFlowGraphPortDesc>() * 4);
            for (int i = 0; i < 4; i++) Marshal.StructureToPtr(ports[i], IntPtr.Add(pPorts, i * Marshal.SizeOf<ExFlowGraphPortDesc>()), false);
            IntPtr typeName = Util.StrDupW("大模型文本");
            var desc = new ExFlowGraphCardDescriptor
            {
                cardType = FLOWGRAPH_CARD_TYPE_LLM_TEXT,
                typeName = typeName,
                tagColor = Util.ExARGB(80, 80, 180, 255),
                portCount = 4,
                ports = pPorts
            };
            flowGraph.RegisterCardType(desc);
            FreeCardRegistrationData(pPorts, 4, typeName);
        }
        
        // 注册：文本显示 (不变)
        static private void RegisterTextRenderCard()
        {
            var ports = new ExFlowGraphPortDesc[4];
            ports[0] = new ExFlowGraphPortDesc { portType = FLOWGRAPH_PORTTYPE_INPUT, dataType = FLOWGRAPH_DATATYPE_STRING, name = Util.StrDupW("输入文本") };
            ports[1] = new ExFlowGraphPortDesc { portType = FLOWGRAPH_PORTTYPE_INTERMEDIATE, dataType = FLOWGRAPH_DATATYPE_STRING, name = Util.StrDupW("显示文本"), widgetType = FLOWGRAPH_NODEDATA_TYPE_TEXT, widgetWidth = 500, defaultWidgetData = Util.StrDupW("") };
            ports[2] = new ExFlowGraphPortDesc { portType = FLOWGRAPH_PORTTYPE_INTERMEDIATE, dataType = FLOWGRAPH_DATATYPE_STRING, name = Util.StrDupW("复制文本"), widgetType = FLOWGRAPH_NODEDATA_TYPE_BUTTON, widgetWidth = 300, widgetHeight = 30, defaultWidgetData = g_btnCopy };
            ports[3] = new ExFlowGraphPortDesc { portType = FLOWGRAPH_PORTTYPE_OUTPUT, dataType = FLOWGRAPH_DATATYPE_STRING, name = Util.StrDupW("输出文本") };
            IntPtr pPorts = ExAPI.Ex_MemAlloc(Marshal.SizeOf<ExFlowGraphPortDesc>() * 4);
            for (int i = 0; i < 4; i++) Marshal.StructureToPtr(ports[i], IntPtr.Add(pPorts, i * Marshal.SizeOf<ExFlowGraphPortDesc>()), false);
            IntPtr typeName = Util.StrDupW("文本显示");
            var desc = new ExFlowGraphCardDescriptor
            {
                cardType = FLOWGRAPH_CARD_TYPE_TEXT_RENDER,
                typeName = typeName,
                tagColor = Util.ExARGB(60, 120, 160, 255),
                portCount = 4,
                ports = pPorts
            };
            flowGraph.RegisterCardType(desc);
            FreeCardRegistrationData(pPorts, 4, typeName);
        }

        public static AIAgent CreateOllamaAgent(string name, string endpoint, string model, string instructions, IList<AITool> tools)
        {
            var client = new OllamaApiClient(new Uri(endpoint), model);
            client.OutgoingJsonSerializerOptions.TypeInfoResolver = AotJsonConfig.Options.TypeInfoResolver;
            client.IncomingJsonSerializerOptions.TypeInfoResolver = AotJsonConfig.Options.TypeInfoResolver;
            return client.AsAIAgent(new ChatClientAgentOptions
            {
                Name = name,
                ChatOptions = new ChatOptions
                {
                    Instructions = instructions,
                    MaxOutputTokens = maxOutputTokens,
                    Reasoning = new() { Effort = ReasoningEffort.Low },
                    Tools = tools
                }
            });
        }
    }
}

public static class AotJsonHelper
{
    /// <summary>
    /// 在 AOT 环境下安全序列化 AI 工具调用的参数 (IReadOnlyDictionary<string, object?>)
    /// </summary>
    public static string SafeSerializeArguments(IDictionary<string, object?>? args)
    {
        if (args == null || args.Count == 0) return "{}";

        var jsonObj = new JsonObject();
        foreach (var kvp in args)
        {
            var key = kvp.Key;
            var value = kvp.Value;

            if (value is null) jsonObj[key] = null;
            else if (value is JsonElement je) jsonObj[key] = JsonNode.Parse(je.GetRawText());
            else if (value is JsonNode jn) jsonObj[key] = jn;
            else if (value is string s) jsonObj[key] = s;
            else if (value is bool b) jsonObj[key] = b;
            else if (value is int i) jsonObj[key] = i;       // 🟢 新增
            else if (value is long l) jsonObj[key] = l;      // 🟢 新增
            else if (value is double d) jsonObj[key] = d;    // 🟢 新增
            else
            {
                try
                {
                    var rawJson = JsonSerializer.Serialize(value, value.GetType(), AotJsonConfig.Options);
                    jsonObj[key] = JsonNode.Parse(rawJson);
                }
                catch { jsonObj[key] = value.ToString(); }
            }
        }
        return jsonObj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}

public static class AotJsonConfig
{
    private static JsonSerializerOptions? _options;

    public static JsonSerializerOptions Options
    {
        get
        {
            if (_options != null) return _options;

            // 1. 加载你项目自己的 Context
            var resolvers = new List<IJsonTypeInfoResolver> { AppJsonContext.Default };

            // 2. 反射加载 OllamaSharp 内部的 Context (防止 AOT 裁剪导致找不到)
            try
            {
                var assembly = typeof(OllamaApiClient).Assembly;
                var contextType = assembly.GetType("OllamaSharp.Models.JsonSourceGenerationContext");
                if (contextType != null)
                {
                    var defaultProp = contextType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);
                    if (defaultProp?.GetValue(null) is JsonSerializerContext internalContext)
                    {
                        resolvers.Add(internalContext);
                    }
                }
            }
            catch { /* 忽略获取失败 */ }

            // 3. 合并解析器
            var combinedResolver = JsonTypeInfoResolver.Combine(resolvers.ToArray());

            _options = new JsonSerializerOptions
            {
                TypeInfoResolver = combinedResolver,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            return _options;
        }
    }

    // 专门用于 AIFunctionFactory 的配置
    public static AIFunctionFactoryOptions FunctionOptions => new AIFunctionFactoryOptions
    {
        SerializerOptions = Options
    };
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(List<string>))]                           
[JsonSerializable(typeof(List<string?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<Dictionary<string, string>>))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]           
[JsonSerializable(typeof(IDictionary<string, object>))]            
internal partial class AppJsonContext : JsonSerializerContext;