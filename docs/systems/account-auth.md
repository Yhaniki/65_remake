# 帳號與登入（Account Auth）

> Phase 1 **不做**。MVP 起用。

## 職責

- Steam 登入
- PlayFab 帳號綁定
- Session / 設定同步

## MVP 流程

```
Steam Login → SteamId
PlayFab LoginWithCustomID(SteamId)
→ 進入大廳
```

## 技術

- **Facepunch Steamworks** — 客户端 Steam API
- **PlayFab** — Custom ID = Steam ID；存戰績、skin、Normal 勝負

## Phase 1

無登入；Unity 直接進選歌。

## 相關

- [architecture/online-services.md](../architecture/online-services.md)
- [01-login/spec.md](../screens/01-login/spec.md)
