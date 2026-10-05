# Daminion API Quick Reference Card

**Version**: 2.0.0 | **Module**: `src.core.daminion_api`

---

## Quick Start

```python
from src.core.daminion_api import DaminionAPI

with DaminionAPI(
    base_url="https://your-server.daminion.net",
    username="admin",
    password="secret"
) as api:
    # Your code here
    items = api.media_items.search(query="city")
```

---

## Common Operations

### Search

```python
# Text search
items = api.media_items.search(query="landscape")

# Structured search (tag ID 13, value 4949)
items = api.media_items.search(
    query_line="13,4949",
    operators="13,any"
)

# Get count only
count = api.media_items.get_count(query_line="13,4949", operators="13,any")
```

### Tags

```python
# Get tag schema
tags = api.tags.get_all_tags()
keywords_tag = next(t for t in tags if t.name == "Keywords")

# Get tag values
values = api.tags.get_tag_values(tag_id=keywords_tag.id)

# Search tag values
matches = api.tags.find_tag_values(tag_id=13, filter_text="city")
```

### Collections

```python
# List collections
collections = api.collections.get_all()

# Get items in collection
items = api.collections.get_items(collection_id=42)

# Create collection
new_id = api.collections.create(
    name="Best Photos",
    item_ids=[123, 456]
)
```

### Metadata

```python
# Get item metadata
metadata = api.item_data.get(item_id=123, get_all=True)

# Batch update tags
api.item_data.batch_update(
    item_ids=[123, 456],
    operations=[{
        "guid": "tag-guid-here",
        "id": 4949,
        "remove": False
    }]
)
```

### Downloads

```python
# Get thumbnail
thumb = api.thumbnails.get(item_id=123, width=200, height=200)

# Download original
file = api.downloads.get_original(item_id=123)

# Save to disk
with open("image.jpg", "wb") as f:
    f.write(file)
```

---

## API Structure

```
api.media_items   → Search, get, delete, favorites, approve
api.tags          → Schema, values, create, update, delete
api.collections   → List, create, update, delete
api.item_data     → Metadata, batch updates
api.settings      → Version, rights, config
api.thumbnails    → Thumbnails, previews
api.downloads     → Original files, exports
api.imports       → Upload, import by URL
api.user_manager  → Users, roles, permissions
```

---

## Error Handling

```python
from src.core.daminion_api import (
    DaminionAuthenticationError,
    DaminionNotFoundError,
    DaminionRateLimitError,
    DaminionAPIError
)

try:
    with DaminionAPI(url, user, pass) as api:
        items = api.media_items.search(query="test")
except DaminionAuthenticationError:
    print("Login failed")
except DaminionNotFoundError:
    print("Resource not found")
except DaminionRateLimitError:
    print("Rate limit exceeded")
except DaminionAPIError as e:
    print(f"API error: {e}")
```

---

## Common Patterns

### Pattern: Search by Keyword

```python
# 1. Get tag schema
tags = api.tags.get_all_tags()
keywords_tag = next(t for t in tags if t.name == "Keywords")

# 2. Find keyword value
values = api.tags.find_tag_values(keywords_tag.id, "city")
value_id = values[0].id

# 3. Search items
items = api.media_items.search(
    query_line=f"{keywords_tag.id},{value_id}",
    operators=f"{keywords_tag.id},any"
)
```

### Pattern: Batch Tag Items

```python
# 1. Search for items
items = api.media_items.search(query="landscape")
item_ids = [item['id'] for item in items]

# 2. Get tag info
tags = api.tags.get_all_tags()
categories = next(t for t in tags if t.name == "Categories")

# 3. Find category value
values = api.tags.find_tag_values(categories.id, "Nature")
category_id = values[0].id

# 4. Apply to all items
api.item_data.batch_update(
    item_ids=item_ids,
    operations=[{
        "guid": categories.guid,
        "id": category_id,
        "remove": False
    }]
)
```

### Pattern: Process All Collections

```python
collections = api.collections.get_all()

for coll in collections:
    items = api.collections.get_items(coll.id)
    for item in items:
        # Process each item
        print(f"{coll.name}: {item['filename']}")
```

### Pattern: Pagination

```python
def get_all_items(api, query):
    all_items = []
    page_size = 500
    index = 0
    
    while True:
        batch = api.media_items.search(
            query=query,
            index=index,
            page_size=page_size
        )
        
        if not batch:
            break
        
        all_items.extend(batch)
        index += page_size
    
    return all_items
```

---

## Configuration Options

```python
api = DaminionAPI(
    base_url="https://server.daminion.net",
    username="admin",
    password="secret",
    catalog_id=1,              # Optional: for multi-catalog servers
    rate_limit=0.1,            # Seconds between requests (default: 0.1)
    timeout=30                 # Request timeout in seconds (default: 30)
)
```

---

## Data Classes

```python
# TagInfo
tag = TagInfo(id=13, guid="...", name="Keywords", type="indexed")

# TagValue
value = TagValue(id=4949, text="city", count=123)

# SharedCollection
coll = SharedCollection(
    id=42,
    name="Best Photos",
    code="ABC123",
    item_count=50,
    created="2026-01-01",
    modified="2026-01-18"
)
```

---

## Best Practices

1. ✅ **Use context manager** - Auto-handles login/logout
2. ✅ **Cache tag schema** - Doesn't change during session
3. ✅ **Use pagination** - For large result sets
4. ✅ **Batch operations** - Update multiple items at once
5. ✅ **Handle errors specifically** - Use typed exceptions
6. ✅ **Configure rate limit** - Adjust based on server capacity

---

## Common Tag IDs

*These are typical but may vary - always verify with `api.tags.get_all_tags()`*

| Tag | Int ID | Indexed ID |
|-----|--------|------------|
| Keywords | 13 | 5000 |
| Categories | 14 | 5001 |
| Description | 67 | - |
| Rating | 64 | - |
| Flag | 42 | - |
| Color Label | 74 | - |

---

## Resources

- **Full Documentation**: `src/core/DAMINION_API.md`
- **Examples**: `src/core/daminion_api_example.py`
- **Official API**: https://marketing.daminion.net/APIHelp
- **Summary**: `.gemini/DAMINION_API_REWRITE_SUMMARY.md`

---

**Print this page for quick reference while coding!**
