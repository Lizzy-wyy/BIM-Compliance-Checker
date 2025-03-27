# main.py
import os
import re
import json
import sys
from openai import OpenAI
from dotenv import load_dotenv

# ------------------------------
# 1. 环境配置
# ------------------------------
load_dotenv()
DEEPSEEK_API_KEY = os.getenv("DEEPSEEK_API_KEY")

# ------------------------------
# 原始文本
#   ↓（预处理：本地过滤）
# 精简文本 → [长度截断]
#   ↓（API调用：DeepSeek）
# JSON数据
#   ↓（本地保存）
# firewall_rules.json
# ------------------------------

# ------------------------------
# 2. 规范预处理（增强建筑类型识别）
# ------------------------------

def preprocess_text(text):
    """
    优化后的预处理函数，识别建筑类型关键词 re模块处理字符串
    """
    patterns = [
        r"第[\d\.]+条",  # 匹配数字条款（如6.1.1）
        r"(高层厂房|仓库|民用建筑|工业建筑|防火墙|耐火极限)\b",
        r"\bGB\d+[\-\.]\d+",
        r"\d+\.?\d*\s*[mh]"
    ]

    clauses = []
    for line in text.split('\n'):
        line = line.strip()
        if any(re.search(p, line) for p in patterns):
            # 合并跨行条款
            if clauses and re.match(r"^\d+\.\d+", line):
                clauses[-1] += line
            else:
                clauses.append(line)
    return '\n'.join(clauses[:30])  # 放宽条款数量限制


# ------------------------------
# 3. API解析模块（支持建筑类型条件）
# ------------------------------
def parse_with_deepseek(text):
    client = OpenAI(
        api_key=DEEPSEEK_API_KEY,
        base_url="https://api.deepseek.com/v1"
    )

    prompt = f"""请将以下防火规范转换为JSON数组，严格按此格式：
{{
  "rules": [
    {{
      "clause": "条款号（如GB50016-6.1.1）",
      "parameter": "参数名（从Wall_Height, FireRating, Wall_Thickness, Distance中选择）",
      "condition": "条件表达式（包含建筑类型时使用'建筑类型:条件'格式）",
      "unit": "单位",
      "note": "特殊说明（可选）"
    }}
  ]
}}

示例转换：
输入：6.1.1 当高层厂房屋顶承重结构耐火极限低于1h时，防火墙应高出屋面0.5m以上
输出：
{{
  "clause": "GB50016-6.1.1",
  "parameter": "Wall_Height",
  "condition": "高层厂房:>=0.5",
  "unit": "m",
  "note": "当屋顶耐火极限<1h时生效"
}}

待解析文本：
{text}"""

    try:
        response = client.chat.completions.create(
            model="deepseek-reasoner",
            messages=[{"role": "user", "content": prompt}],
            temperature=0.1,
            max_tokens=800
        )

        raw_output = response.choices[0].message.content
        return re.sub(r'```json|```', '', raw_output).strip()
    except Exception as e:
        print(f"API调用失败：{str(e)}")
        sys.exit(1)


# ------------------------------
# 4. 主程序（增强路径处理）
# ------------------------------
if __name__ == "__main__":
    try:
        # 读取规范文件
        input_file = os.path.abspath("regulations.txt")
        with open(input_file, "r", encoding="utf-8") as f:
            raw_text = f.read()

        # 预处理
        processed_text = preprocess_text(raw_text)[:3000]  # 放宽输入长度

        # 解析
        json_rules = parse_with_deepseek(processed_text)

        # 保存结果
        output_file = os.path.abspath("firewall_rules.json")
        with open(output_file, "w", encoding="utf-8") as f:
            json.dump(json.loads(json_rules), f, ensure_ascii=False, indent=2)

        print(f"✅ 成功生成 {output_file}")

    except Exception as e:
        print(f"❌ 错误：{str(e)}")
        sys.exit(1)