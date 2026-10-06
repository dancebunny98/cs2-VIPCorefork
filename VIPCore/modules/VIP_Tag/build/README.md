# Allows you to change the clan tag in the tab for the VIP player

# Config

### in vip.json
`"Tag": [ "ADMIN", "ADMINISTRATOR" ]` // you can add your own value here

The last value in `Tag` is applied automatically to the player's chat and TAB
(`OWNER` in `"Tag": [ "ADMIN", "OWNER" ]`). Players can choose `Disable` to
hide the tag in both places; that choice is saved until they select a tag again.

Add `DisplayTag` to control the label shown after the player's name in `!vips`:

`"DisplayTag": "VIP"`

# in translations

RU:

`"tag.Disable": "Выключить"`

`"tag.On": "Вы установили значение {blue}Tag {default}на {red}{0}"`

`"tag.Off": "Вы выключили {blue}Tag"`

EN:

`"tag.Disable": "Disable"`

`"tag.On": "You have set {blue}Tag {default} to {red}{0}"`

`"tag.Off": "You've disabled the {blue}Tag"`
