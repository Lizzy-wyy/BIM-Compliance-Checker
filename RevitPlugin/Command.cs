using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using Autodesk.Revit.Attributes;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Text;

namespace ComplianceChecker
{
    [Transaction(TransactionMode.Manual)]
    public class Command : IExternalCommand
    {
        public class ComplianceRule
        {
            public string clause { get; set; }
            public string parameter { get; set; }
            public string condition { get; set; }
            public string unit { get; set; }
            public string note { get; set; }
        }

        // 参数映射表（关键修改点）
        private Dictionary<string, (Guid guid, BuiltInParameter builtInParam)> paramMap = new Dictionary<string, (Guid, BuiltInParameter)>
        {
            { "Wall_Height", (Guid.Empty, BuiltInParameter.WALL_USER_HEIGHT_PARAM) },
            { "FireRating", (Guid.Empty, BuiltInParameter.FIRE_RATING) },
            { "Distance", (new Guid("0af14e6a-00a9-4236-849d-4800b3813c11"), BuiltInParameter.INVALID) }
        };

        private Dictionary<string, double> unitConverter = new Dictionary<string, double>
        {
            { "m", 3.28084 }, { "h", 1 }, { "default", 1 }
        };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;
            List<string> messages = new List<string>();

            try
            {
                string rulePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    @"我的\新学习\revit二次开发\revit_rules\firewall_rules.json");

                var rules = LoadRules(rulePath);

                using (Transaction trans = new Transaction(doc, "智能防火检查"))
                {
                    trans.Start();

                    var walls = new FilteredElementCollector(doc)
                        .OfClass(typeof(Wall))
                        .WhereElementIsNotElementType();

                    foreach (Wall wall in walls)
                    {
                        // 获取建筑类型参数
                        Parameter buildingTypeParam = wall.get_Parameter(
                            new Guid("6b552857-3fe6-4426-b964-f01442fff42d"));

                        string buildingType = buildingTypeParam?.AsString() ?? "未分类";

                        // 检查每个规则
                        foreach (var rule in rules)
                        {
                            CheckWallCompliance(wall, buildingType, rule, messages);
                        }
                    }

                    trans.Commit();
                }

                ShowResults(messages);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("严重错误", $"主程序异常：{ex.ToString()}");
                return Result.Failed;
            }
        }

        private List<ComplianceRule> LoadRules(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"规则文件未找到：{path}");

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string json = reader.ReadToEnd();
                var ruleData = JsonConvert.DeserializeObject<Dictionary<string, List<ComplianceRule>>>(json);
                return ruleData["rules"];
            }
        }

        private void CheckWallCompliance(Wall wall, string buildingType, ComplianceRule rule, List<string> messages)
        {
            try
            {
                if (!paramMap.TryGetValue(rule.parameter, out var paramInfo))
                {
                    messages.Add($"[配置错误] 参数 {rule.parameter} 未配置映射");
                    return;
                }

                Parameter param = paramInfo.builtInParam == BuiltInParameter.INVALID ?
                    wall.get_Parameter(paramInfo.guid) :
                    wall.get_Parameter(paramInfo.builtInParam);

                if (param == null || !param.HasValue)
                {
                    messages.Add($"[数据缺失] 墙体 {wall.Id} 缺少 {rule.parameter} 参数");
                    return;
                }

                // 解析条件表达式
                var (targetBuildingType, condition) = ParseCondition(rule.condition);

                // 建筑类型匹配检查
                if (!string.IsNullOrEmpty(targetBuildingType) &&
                    !buildingType.Contains(targetBuildingType))
                {
                    return;
                }

                double value = param.AsDouble();
                if (unitConverter.TryGetValue(rule.unit, out double conversion))
                {
                    value /= conversion; // 转换为标准单位
                }

                if (!EvaluateCondition(value, condition))
                {
                    messages.Add(
                        $"【{rule.clause}】墙体 {wall.Id} ({buildingType})\n" +
                        $"参数：{rule.parameter}\n" +
                        $"当前值：{value / conversion:F2}{rule.unit}\n" +  // 显示原始单位
                        $"要求：{rule.condition}\n" +
                        $"备注：{rule.note}");
                }
            }
            catch (Exception ex)
            {
                messages.Add($"[执行错误] 检查 {rule.clause} 时出错：{ex.Message}");
            }
        }

        private (string buildingType, string condition) ParseCondition(string input)
        {
            var match = Regex.Match(input, @"^(.+?):\s*([<>=]+.*)");
            return match.Success ?
                (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim()) :
                (null, input);
        }

        private bool EvaluateCondition(double value, string condition)
        {
            var match = Regex.Match(condition, @"([<>=]+)\s*([\d.]+)");
            if (!match.Success) return false;

            string op = match.Groups[1].Value.Trim();
            double threshold = double.Parse(match.Groups[2].Value);

            switch (op)
            {
                case ">=": return value >= threshold;
                case "<=": return value <= threshold;
                case ">": return value > threshold;
                case "<": return value < threshold;
                default: return Math.Abs(value - threshold) < 0.001;
            }
        }

        private void ShowResults(List<string> messages)
        {
            var dialog = new TaskDialog("防火规范检查结果");
            dialog.MainInstruction = $"发现 {messages.Count} 个合规问题";
            dialog.TitleAutoPrefix = false;

            if (messages.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                foreach (var msg in messages)
                {
                    sb.AppendLine("• " + msg.Replace("\n", "\n   "));
                }
                dialog.MainContent = sb.ToString();
            }
            else
            {
                dialog.MainContent = "所有检查项符合防火规范要求";
            }

            dialog.CommonButtons = TaskDialogCommonButtons.Ok;
            dialog.Show();
        }
    }
}
