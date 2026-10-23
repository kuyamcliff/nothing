# ClipFx Share protocol v1

Local network only. No internet, no cloud, no accounts.

## Discovery — UDP 47631
JSON datagrams, sent to multicast `239.255.47.63`, the limited broadcast address and every interface's directed broadcast.

    {"cfs":1,"t":"announce"|"query","device":{"id","name","platform","port"}}

A `query` is answered with a unicast `announce`. Devices announce every 3 s and expire after 12 s.
If UDP is blocked, clients may probe TCP `hello` across their /24 subnets.

## Transfer — TCP 47630 (falls back to a free port, announced in discovery)
Each request is one UTF-8 JSON line ending in `\n`; each reply is one JSON line. Empty lines are keep-alives.
Connections are reused for many requests.

| op | request | reply |
| --- | --- | --- |
| hello | `{}` | `{"ok":true,"device":{…}}` |
| offer | `session, from, items[], totalSize` | `{"ok":true,"accepted":bool,"key","have":[{"i","r":[[s,e],…]}],"reason"}` |
| status | `session, key` | `{"ok":true,"state","have":[…]}` |
| data | `session, key, parts:[{"i","off","len"}]` then for each part `len` raw bytes + 4-byte big-endian CRC-32 | `{"ok":true}` or `{"ok":false,"error":"paused"|"cancelled"|"crc"|…}` |
| pause / resume | `session, key` | `{"ok":true}` |
| done | `session, key` | `{"ok":true}` or `{"ok":false,"error":"incomplete","have":[…]}` |
| cancel | `session, key` | `{"ok":true}` |

Items: `{"i","path","size","mtime","type":"file"|"dir"|"text","text"}`. Paths use `/` and are relative.
Offering a session the receiver already accepted resumes it without asking again, returning the byte ranges it has.
