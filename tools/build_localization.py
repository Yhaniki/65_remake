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
    "room.start_stub":      ["Gameplay hand-off pending (this milestone)", "遊戲銜接待整合（本階段）", "游戏衔接待整合（本阶段）", "ゲーム連携は未実装（本段階）"],

    "mode.free":   ["Free", "自由", "自由", "フリー"],
    "mode.normal": ["Normal", "普通", "普通", "ノーマル"],

    "status.waiting": ["Waiting", "等待中", "等待中", "待機中"],
    "status.ingame":  ["In Game", "遊戲中", "游戏中", "ゲーム中"],

    "songselect.title":     ["Select Song", "選擇歌曲", "选择歌曲", "曲を選択"],
    "songselect.random":    ["Random", "隨機", "随机", "ランダム"],
    "songselect.stage":     ["Stage", "舞台", "舞台", "ステージ"],
    "songselect.search":    ["Search song / artist…", "搜尋歌曲 / 歌手…", "搜索歌曲 / 歌手…", "曲 / アーティスト検索…"],
    "songselect.bpm":       ["BPM {0}", "BPM {0}", "BPM {0}", "BPM {0}"],
    "songselect.level":     ["Lv {0}", "Lv {0}", "Lv {0}", "Lv {0}"],
    "songselect.page":      ["Page {0}/{1}", "第 {0}/{1} 頁", "第 {0}/{1} 页", "{0}/{1} ページ"],
    "songselect.prev":      ["Prev", "上一頁", "上一页", "前へ"],
    "songselect.next":      ["Next", "下一頁", "下一页", "次へ"],
    "songselect.no_songs":  ["No songs found", "找不到歌曲", "找不到歌曲", "曲が見つかりません"],
    "songselect.confirm":   ["Confirm", "選定", "选定", "決定"],
    "songselect.need_pick": ["Please pick a song first", "請先選擇歌曲", "请先选择歌曲", "先に曲を選んでください"],

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
