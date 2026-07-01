#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Generate the four UI localization tables from a single source dict.

Writes StreamingAssets/Localization/{en,zh-TW,zh-Hans,ja}.json in the JsonUtility
array schema:  { "language","name","culture","entries":[{"k","v"},...] }

Keeping one source guarantees all languages share the exact same key set
(LocalizationTests.AllLanguages_HaveSameKeySet relies on this).
"""
import json, os

LANGS = [
    ("en",      "English",  "en-US"),
    ("zh-TW",   "繁體中文", "zh-TW"),
    ("zh-Hans", "简体中文", "zh-CN"),
    ("ja",      "日本語",   "ja-JP"),
]
ORDER = ["en", "zh-TW", "zh-Hans", "ja"]

# key: [en, zh-TW, zh-Hans, ja]
STR = {
    "common.apply":   ["Apply", "套用", "应用", "適用"],
    "common.back":    ["Back", "返回", "返回", "戻る"],
    "common.cancel":  ["Cancel", "取消", "取消", "キャンセル"],
    "common.close":   ["Close", "關閉", "关闭", "閉じる"],
    "common.confirm": ["Confirm", "確定", "确定", "確定"],
    "common.off":     ["Off", "關", "关", "オフ"],
    "common.on":      ["On", "開", "开", "オン"],

    "app.title": ["Dance Online", "熱舞 Online", "热舞 Online", "ダンス Online"],

    "lobby.create_room":    ["Create Room", "建立房間", "创建房间", "ルーム作成"],
    "lobby.rooms":          ["Rooms", "房間列表", "房间列表", "ルーム一覧"],
    "lobby.online_players": ["Online Players", "線上玩家", "在线玩家", "オンラインプレイヤー"],
    "lobby.chat":           ["Chat", "聊天", "聊天", "チャット"],
    "lobby.chat_hint":      ["Type a message and press Enter…", "輸入訊息後按 Enter…", "输入消息后按 Enter…", "メッセージを入力して Enter…"],
    "lobby.send":           ["Send", "送出", "发送", "送信"],
    "lobby.settings":       ["Settings", "設定", "设置", "設定"],
    "lobby.logout":         ["Logout", "登出", "登出", "ログアウト"],
    "lobby.select_note":    ["Notes", "選擇音符", "选择音符", "ノーツ選択"],
    "lobby.empty":          ["No rooms yet — create one!", "目前沒有房間，來建立一間吧！", "目前没有房间，来建一个吧！", "ルームがありません。作成しましょう！"],
    "lobby.col_room":       ["Room", "房號", "房号", "番号"],
    "lobby.col_host":       ["Host", "房主", "房主", "ホスト"],
    "lobby.col_count":      ["Players", "人數", "人数", "人数"],
    "lobby.col_mode":       ["Mode", "模式", "模式", "モード"],
    "lobby.col_status":     ["Status", "狀態", "状态", "状態"],

    "room.title":           ["Room {0}", "房間 {0}", "房间 {0}", "ルーム {0}"],
    "room.server_name":     ["Free Practice {0}", "自由練習場{0}", "自由练习场{0}", "自由練習場{0}"],
    "room.channel":         ["Channel {0}", "頻道{0}", "频道{0}", "チャンネル{0}"],
    "room.default_name":    ["{0}'s Dance Room", "{0}的舞蹈室", "{0}的舞蹈室", "{0}のダンスルーム"],
    "room.leave":           ["Leave Room", "離開房間", "离开房间", "ルーム退出"],
    "room.ready":           ["Ready", "準備", "准备", "準備"],
    "room.cancel_ready":    ["Cancel", "取消準備", "取消准备", "準備解除"],
    "room.start":           ["Start", "開始", "开始", "開始"],
    "room.select_song":     ["Select Song", "選擇歌曲", "选择歌曲", "曲を選択"],
    "room.no_song":         ["(No song selected)", "（尚未選歌）", "（尚未选歌）", "（曲未選択）"],
    "room.mode":            ["Mode", "模式", "模式", "モード"],
    "room.empty_seat":      ["Empty", "空位", "空位", "空席"],
    "room.host":            ["Host", "房主", "房主", "ホスト"],
    "room.waiting_players": ["Some players are not ready", "尚有玩家未準備", "还有玩家未准备", "未準備のプレイヤーがいます"],
    "room.need_song":       ["Please select a song first", "請先選擇歌曲", "请先选择歌曲", "先に曲を選択してください"],
    "room.drop_up":         ["Up", "向上", "向上", "上"],
    "room.drop_down":       ["Down", "向下", "向下", "下"],
    "room.drop_tilt":       ["Tilt", "傾斜", "倾斜", "斜め"],

    "result.clear":     ["Clear!", "完成!", "完成!", "クリア!"],
    "result.failed":    ["Failed", "失敗", "失败", "失敗"],
    "result.score":     ["Score", "分數", "分数", "スコア"],
    "result.max_combo": ["Max Combo", "最大連段", "最大连段", "最大コンボ"],
    "result.back":      ["Back to Room", "返回房間", "返回房间", "ルームに戻る"],

    "mode.free":   ["Free", "自由", "自由", "フリー"],
    "mode.normal": ["Normal", "普通", "普通", "ノーマル"],

    "status.waiting": ["Waiting", "等待中", "等待中", "待機中"],
    "status.ingame":  ["In Game", "遊戲中", "游戏中", "ゲーム中"],

    "songselect.title":     ["Select Song", "選擇歌曲", "选择歌曲", "曲を選択"],
    "songselect.random":    ["Random", "隨機場景", "随机场景", "ランダム"],
    "songselect.stage":     ["Stage", "舞台", "舞台", "ステージ"],
    "songselect.search":    ["Type a keyword and press Enter", "輸入關鍵字並送出", "输入关键字并回车", "キーワードを入力してEnter"],
    "songselect.bpm":       ["BPM {0}", "BPM {0}", "BPM {0}", "BPM {0}"],
    "songselect.level":     ["Lv {0}", "Lv {0}", "Lv {0}", "Lv {0}"],
    "songselect.page":      ["Page {0}/{1}", "第 {0}/{1} 頁", "第 {0}/{1} 页", "{0}/{1} ページ"],
    "songselect.prev":      ["Prev", "上一頁", "上一页", "前へ"],
    "songselect.next":      ["Next", "下一頁", "下一页", "次へ"],
    "songselect.no_songs":  ["No songs found", "找不到歌曲", "找不到歌曲", "曲が見つかりません"],
    "songselect.confirm":   ["Confirm", "選定", "选定", "決定"],
    "songselect.need_pick": ["Please pick a song first", "請先選擇歌曲", "请先选择歌曲", "先に曲を選んでください"],
    # NOTE: 場景選擇 caption + 演唱者/BPM info labels are BAKED into MUSICSELDLG art (and 音符數 uses lbl_notes.an),
    # so they are NOT text-localized here — the song-select screen draws values only. See SongSelectScreen.cs.
    "songselect.mode_free":   ["Free Mode", "自由模式", "自由模式", "フリーモード"],
    "songselect.mode_normal": ["Normal Mode", "普通模式", "普通模式", "ノーマルモード"],
    "songselect.form_basic":  ["Basic", "基本", "基本", "基本"],
    "songselect.form_fan":    ["Fan", "扇形", "扇形", "扇形"],
    "songselect.form_ring":   ["Ring", "環繞", "环绕", "リング"],
    "songselect.form_random": ["Random", "隨機", "随机", "ランダム"],

    # 隨機 tab difficulty-range rows
    "songselect.rand_1_5":  ["Random Lv 1-5",  "隨機難度 1-5",    "随机难度 1-5",    "ランダム難易度 1-5"],
    "songselect.rand_1_9":  ["Random Lv 1-9",  "隨機難度 1-9",    "随机难度 1-9",    "ランダム難易度 1-9"],
    "songselect.rand_5_9":  ["Random Lv 5-9",  "隨機難度 5-9",    "随机难度 5-9",    "ランダム難易度 5-9"],
    "songselect.rand_all":  ["Random Lv All",  "隨機難度 全部",   "随机难度 全部",   "ランダム難易度 全部"],
    "songselect.rand_5up":  ["Random Lv 5+",   "隨機難度 5級以上",  "随机难度 5级以上",  "ランダム難易度 5以上"],
    "songselect.rand_9up":  ["Random Lv 9+",   "隨機難度 9級以上",  "随机难度 9级以上",  "ランダム難易度 9以上"],
    "songselect.rand_13up": ["Random Lv 13+",  "隨機難度 13級以上", "随机难度 13级以上", "ランダム難易度 13以上"],

    # 3D stage names — selector scenes (ids 0..30) + special rooms (31/32/33/35/37/38/39).
    # zh-TW are the real EXE names; en/ja are translations (proper nouns like NARNIA kept as-is).
    "stage.name.0":  ["Pedestrian Street", "步行街", "步行街", "歩行者天国"],
    "stage.name.1":  ["New World", "新天地", "新天地", "新天地"],
    "stage.name.2":  ["Garage", "車庫", "车库", "ガレージ"],
    "stage.name.3":  ["Stage", "舞台", "舞台", "ステージ"],
    "stage.name.4":  ["Beach", "海灘", "海滩", "ビーチ"],
    "stage.name.5":  ["Christmas Eve", "聖誕夜", "圣诞夜", "クリスマスイブ"],
    "stage.name.6":  ["Amusement Park", "遊樂場", "游乐场", "遊園地"],
    "stage.name.7":  ["Polar Garden", "極地花園", "极地花园", "極地の花園"],
    "stage.name.8":  ["Egyptian Tomb", "埃及古墓", "埃及古墓", "エジプトの墓"],
    "stage.name.9":  ["Black & White Ball", "黑白舞會", "黑白舞会", "白黒の舞踏会"],
    "stage.name.10": ["Parade Float", "花車", "花车", "フロート"],
    "stage.name.11": ["Dance Grand Meet", "舞林大會", "舞林大会", "ダンス大会"],
    "stage.name.12": ["Soccer Field (Day)", "足球場 (日)", "足球场 (日)", "サッカー場（昼）"],
    "stage.name.13": ["Soccer Field (Night)", "足球場 (夜)", "足球场 (夜)", "サッカー場（夜）"],
    "stage.name.14": ["Seabed", "海底", "海底", "海底"],
    "stage.name.15": ["Magic House", "魔法屋", "魔法屋", "魔法の家"],
    "stage.name.16": ["Busy Street", "繁華街道", "繁华街道", "繁華街"],
    "stage.name.17": ["City Subway", "都市地鐵", "都市地铁", "都市の地下鉄"],
    "stage.name.18": ["Luxury Cruise", "豪華郵輪", "豪华邮轮", "豪華客船"],
    "stage.name.19": ["Dance Battle Arena", "舞鬥競技場", "舞斗竞技场", "ダンスバトル闘技場"],
    "stage.name.20": ["Subway Station", "地鐵驛站", "地铁驿站", "地下鉄駅"],
    "stage.name.21": ["Dance Bar", "激舞酒吧", "激舞酒吧", "ダンスバー"],
    "stage.name.22": ["Graveyard", "墓地", "墓地", "墓地"],
    "stage.name.23": ["Classroom", "教室", "教室", "教室"],
    "stage.name.24": ["Snowscape", "雪景", "雪景", "雪景色"],
    "stage.name.25": ["Spring", "春天", "春天", "春"],
    "stage.name.26": ["Basketball Court", "籃球場", "篮球场", "バスケットコート"],
    "stage.name.27": ["NARNIA", "NARNIA", "NARNIA", "NARNIA"],
    "stage.name.28": ["Beijing Night", "北京之夜", "北京之夜", "北京の夜"],
    "stage.name.29": ["Airport", "飛機場", "飞机场", "空港"],
    "stage.name.30": ["Cartoon Road", "卡通公路", "卡通公路", "カートゥーンロード"],
    "stage.name.31": ["Wedding Room A", "婚禮房 A", "婚礼房 A", "ウェディングルーム A"],
    "stage.name.32": ["Wedding Room B", "婚禮房 B", "婚礼房 B", "ウェディングルーム B"],
    "stage.name.33": ["Wedding Room C", "婚禮房 C", "婚礼房 C", "ウェディングルーム C"],
    "stage.name.35": ["My Home", "我的家", "我的家", "マイホーム"],
    "stage.name.37": ["Private Room", "個人房", "个人房", "個人ルーム"],
    "stage.name.38": ["Wedding Hall", "婚禮大廳", "婚礼大厅", "ウェディングホール"],
    "stage.name.39": ["Private Room (Night)", "個人房（夜）", "个人房（夜）", "個人ルーム（夜）"],

    "difficulty.easy":   ["Easy", "簡單", "简单", "かんたん"],
    "difficulty.normal": ["Normal", "普通", "普通", "ふつう"],
    "difficulty.hard":   ["Hard", "困難", "困难", "むずかしい"],

    "note.title": ["Select Notes", "選擇音符", "选择音符", "ノーツ選択"],

    "settings.title":        ["Settings", "設定", "设置", "設定"],
    "settings.tab.video":    ["Video", "畫面", "画面", "画面"],
    "settings.tab.audio":    ["Audio", "音效", "音效", "オーディオ"],
    "settings.tab.language": ["Language", "語言", "语言", "言語"],
    "settings.resolution":   ["Window Size", "視窗大小", "窗口大小", "ウィンドウサイズ"],
    "settings.display_mode": ["Display Mode", "顯示模式", "显示模式", "表示モード"],
    "settings.vsync":        ["VSync", "垂直同步", "垂直同步", "垂直同期"],
    "settings.bgm":          ["BGM Volume", "背景音樂", "背景音乐", "BGM 音量"],
    "settings.game_music":   ["Game Music", "遊戲音樂", "游戏音乐", "ゲーム音楽"],
    "settings.sfx":          ["SFX", "音效", "音效", "効果音"],
    "settings.language":     ["Language", "語言", "语言", "言語"],

    "display.windowed":   ["Windowed", "視窗", "窗口", "ウィンドウ"],
    "display.fullscreen": ["Fullscreen", "全螢幕", "全屏", "フルスクリーン"],
    "display.borderless": ["Borderless", "無邊框全螢幕", "无边框全屏", "ボーダーレス"],

    "join.full":     ["This room is full", "這個房間已滿了", "这个房间已满了", "このルームは満員です"],
    "join.ingame":   ["This room is in game", "這個房間正在遊戲中", "这个房间正在游戏中", "このルームはゲーム中です"],
    "join.notfound": ["Room not found", "房間不存在", "房间不存在", "ルームが見つかりません"],
}


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out_dir = os.path.join(here, "..", "65", "My project", "Assets", "StreamingAssets", "Localization")
    out_dir = os.path.normpath(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    for li, (code, name, culture) in enumerate(LANGS):
        idx = ORDER.index(code)
        entries = [{"k": k, "v": STR[k][idx]} for k in STR]
        doc = {"language": code, "name": name, "culture": culture, "entries": entries}
        path = os.path.join(out_dir, code + ".json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(doc, f, ensure_ascii=False, indent=1)
            f.write("\n")
        print(f"wrote {path}  ({len(entries)} keys)")


if __name__ == "__main__":
    main()
