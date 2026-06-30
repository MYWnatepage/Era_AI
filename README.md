# Era\_AI

Era\_AI 是一个基于 Era / Emuera 的 AI 对话实验项目。项目目标是在 Emuera 剧本中接入外部 AI 模型，让角色能够根据玩家自由输入生成对话回应，并支持短期历史与外部长期记忆。

## 当前状态

当前版本为原型测试版，已完成一条可运行的基础链路：

```text
Emuera 剧本
→ 写入 AI/ai\_request.txt
→ AI Bridge 监听并解析请求
→ 调用 Mock / GX / OpenAI-compatible 后端
→ 写入 AI/ai\_response.txt
→ 游戏读取并显示角色回复
→ 写入 cache 长期记忆
```

## 已实现功能

* 标题页与剧本选择
* 雨夜房间测试剧本
* 玩家自由输入对话
* AI 回复显示
* 最近对话历史
* 外部长期记忆
* 保存与读取进度
* 自动存档
* 启动前模型选择
* Mock 测试后端
* GX / OpenAI-compatible 模型接口
* JSON / 类 JSON 请求解析
* 基础错误日志

## 目录结构

```text
Era\_AI/
├─ AI/
│  ├─ ai\_bridge.py
│  ├─ select\_model.py
│  ├─ model\_profiles.ini
│  └─ AI\_CONFIG.example.txt
├─ ERB/
│  ├─ SYSTEM\_MAIN.ERB
│  ├─ SYSTEM\_TITLE.ERB
│  ├─ SYSTEM\_TURNEND.ERB
│  └─ script/
│     └─ SCENARIO\_TEST\_RAIN\_ROOM\_NO\_AI.ERB
├─ CSV/
│  └─ \_replace.csv
├─ start\_game\_ascii.cmd
├─ emuera.config
├─ setting.json
└─ README.md
```

运行时会自动生成或更新以下文件，这些文件不应上传到 Git：

```text
cache/
AI/AI\_CONFIG.txt
AI/ai\_request.txt
AI/ai\_response.txt
AI/model\_\*.txt
AI/ai\_bridge\_error.log
\*.sav
global.sav
profile
emuera.log
```

## 运行方式

1. 准备 Emuera 运行环境。
2. 准备 Python 环境。
3. 根据需要编辑 `AI/model\_profiles.ini`。
4. 双击运行：

```text
start\_game\_ascii.cmd
```

5. 在启动器中选择模型配置。
6. 进入游戏后选择剧本并开始对话。

## 模型配置

实际运行时使用：

```text
AI/AI\_CONFIG.txt
```

该文件可能包含本地接口地址或 API Key，因此不应提交到 Git。仓库中只保留示例文件：

```text
AI/AI\_CONFIG.example.txt
```

示例：

```text
MODEL\_SOURCE=mock
MOCK\_MODEL\_NAME=mock-test

# GX example
# MODEL\_SOURCE=gx
# GX\_MODEL\_NAME=deepseek-v4-flash
# GX\_API\_BASE\_URL=http://127.0.0.1:8000/v1
# GX\_API\_KEY=EMPTY

# OpenAI-compatible example
# MODEL\_SOURCE=openai
# OPENAI\_MODEL=gpt-4.1-mini
# OPENAI\_API\_BASE\_URL=https://api.openai.com/v1
# OPENAI\_API\_KEY=YOUR\_API\_KEY
```

## 当前测试剧本

当前包含一个测试剧本：

```text
ERB/script/SCENARIO\_TEST\_RAIN\_ROOM\_NO\_AI.ERB
```

剧本名：雨夜房间  
角色名：艾拉  
用途：测试自由输入、AI 回复、短期历史、长期记忆与保存读取流程。

## 注意事项

* 本项目仍处于原型阶段。
* AI 回复可能存在风格偏移、重复或不稳定。
* 不要提交 `AI/AI\_CONFIG.txt`，避免泄露 API Key 或本地服务地址。
* 不要提交 `cache/`，其中包含本地对话记忆。
* 不要提交存档文件和 Emuera 可执行文件。
* 仓库不包含 Emuera 程序本体，请自行准备运行环境。

## 后续计划

* 增加更多独立剧本
* 将角色设定与剧本设定集中管理
* 优化长期记忆摘要机制
* 完善设置页与对话风格系统
* 提高 AI 对话流程稳定性

