# 角色外觀（Avatar & Outfit）

> 優先級：**P2 post-MVP**（MVP 用固定預設）
> 使用此 system 的 screen：[07-shop](../screens/07-shop/spec.md), [04-room](../screens/04-room/spec.md), [05-game-arena](../screens/05-game-arena/spec.md)

## 職責

- 角色模型展示
- 服裝穿戴
- 外觀資料存儲

## 原版行為（Reference）

- 3D Q版角色
- 可換髮型、上衣、下裝、鞋子、配件
- 在房間、大廳、遊戲場地都顯示角色外觀
- 服裝從商場購買

## MVP 規格

**MVP 不做換裝。** 所有玩家使用固定預設外觀。

- 可選：提供 2 種預設（男/女），MVP 不開放自訂
- 房間/遊戲中顯示：名字 + 簡單頭像或 placeholder 模型

## post-MVP 規格（草案）

```
Avatar {
  userId: string
  gender: "male" | "female"
  equipped: {
    hair: itemId
    top: itemId
    bottom: itemId
    shoes: itemId
    accessory: itemId | null
  }
}
```

## 待確認

- [ ] MVP 要不要至少選男/女？
- [ ] 原版角色是 Q 版還是寫實比例？
