# Daminion Server Web API - Complete Documentation

**Version**: 2.0.0  
**Last Updated**: 2026-01-18  
**Official API**: https://marketing.daminion.net/APIHelp

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Quick Start](#quick-start)
4. [Authentication](#authentication)
5. [API Reference](#api-reference)
6. [Common Patterns](#common-patterns)
7. [Error Handling](#error-handling)
8. [Best Practices](#best-practices)
9. [Migration Guide](#migration-guide)

---

## Overview

This module provides a **comprehensive, production-ready client** for the Daminion Server Web API. It's designed with:

- ✅ **100% Coverage** of documented API endpoints
- ✅ **Type Safety** with full type hints
- ✅ **Clean Interface** with intuitive method names
- ✅ **Automatic** session management and rate limiting
- ✅ **Robust** error handling with custom exceptions
- ✅ **Well-documented** with docstrings and examples

### Key Features

| Feature | Description |
|---------|-------------|
| **Context Manager** | Automatic authentication and cleanup |
| **Sub-APIs** | Organized into logical modules (media_items, tags, collections, etc.) |
| **Rate Limiting** | Built-in request throttling (configurable) |
| **Session Cookies** | Automatic cookie management |
| **Error Types** | Specific exceptions for different error cases |
| **Type Hints** | Full typing support for IDE autocomplete |

---

## Architecture

The API client is organized into specialized sub-APIs:

```
DaminionAPI (main client)
├── media_items    → MediaItemsAPI
├── tags           → TagsAPI
├── collections    → CollectionsAPI
├── item_data      → ItemDataAPI
├── settings       → SettingsAPI
├── thumbnails     → ThumbnailsAPI
├── downloads      → DownloadsAPI
├── imports        → ImportsAPI
└── user_manager   → UserManagerAPI
```

### Design Principles

1. **Separation of Concerns**: Each sub-API handles a specific domain
2. **Consistent Naming**: Methods follow `verb_noun` pattern (e.g., `get_items`, `create_collection`)
3. **Smart Defaults**: Sensible defaults for pagination, timeouts, etc.
4. **Fail Fast**: Immediate validation and clear error messages

---

## Quick Start

### Installation

```python
from src.core.daminion_api import DaminionAPI
```

### Basic Usage

```python
# Using context manager (recommended)
with DaminionAPI(
    base_url="https://example.daminion.net",
    username="your_username",
    password="your_password"
) as api:
    # Search for media items
    items = api.media_items.search(query="city")
    print(f"Found {len(items)} items")
    
    # Get all collections
    collections = api.collections.get_all()
    for coll in collections:
        print(f"{coll.name}: {coll.item_count} items")
    
    # Get tag values
    keywords = api.tags.get_tag_values(tag_id=13, filter_text="urban")
    for kw in keywords:
        print(f"{kw.text} ({kw.count} items)")
```

### Manual Session Management

```python
api = DaminionAPI(server_url, username, password)

try:
    api.authenticate()
    
    # Your API calls here
    items = api.media_items.search(query="nature")
    
finally:
    api.logout()
```

---

## Authentication

### Login

Authentication happens automatically when using the context manager or by calling `authenticate()`:

```python
api = DaminionAPI(
    base_url="https://example.daminion.net",
    username="admin",
    password="secret123"
)

api.authenticate()  # Establishes session
```

### Multi-Catalog Servers

If your server hosts multiple catalogs:

```python
api = DaminionAPI(
    base_url="https://example.daminion.net",
    username="admin",
    password="secret123",
    catalog_id=1  # Specify catalog ID
)
```

### Session Cookies

Session cookies are **automatically managed**. The client:
- Stores cookies from the login response
- Sends cookies with each subsequent request
- Clears cookies on logout

### Checking Authentication Status

```python
if api.is_authenticated():
    print("Logged in successfully")
else:
    print("Not authenticated")
```

---

## API Reference

### MediaItemsAPI

**Access**: `api.media_items`

#### `search(query, query_line, operators, index, page_size)`

Search for media items using text or structured queries.

**Parameters**:
- `query` (str, optional): Simple text search (e.g., `"city"`)
- `query_line` (str, optional): Structured query (e.g., `"13,4949"` for tag=13, value=4949)
- `operators` (str, optional): Operators per tag (e.g., `"13,any"`)
- `index` (int): Starting index (default: 0)
- `page_size` (int): Items per page (default: 500)

**Returns**: `List[Dict[str, Any]]`

**Examples**:

```python
# Simple text search
items = api.media_items.search(query="landscape")

# Structured search by Keywords tag (ID=13, value=4949)
items = api.media_items.search(
    query_line="13,4949",
    operators="13,any"
)

# Multiple tags (Keywords AND Categories)
items = api.media_items.search(
    query_line="13,4949;14,200",
    operators="13,any;14,all"
)

# Pagination
items_page1 = api.media_items.search(query="nature", index=0, page_size=100)
items_page2 = api.media_items.search(query="nature", index=100, page_size=100)
```

#### `get_by_ids(item_ids)`

Retrieve specific items by their IDs.

**Parameters**:
- `item_ids` (List[int]): List of item IDs

**Returns**: `List[Dict[str, Any]]`

**Example**:

```python
items = api.media_items.get_by_ids([123, 456, 789])
```

#### `get_count(query_line, operators, force)`

Get count of matching items without retrieving full data.

**Parameters**:
- `query_line` (str, optional): Structured query
- `operators` (str, optional): Operators
- `force` (bool): Force refresh of cached count (default: False)

**Returns**: `int`

**Example**:

```python
count = api.media_items.get_count(
    query_line="13,4949",
    operators="13,any"
)
print(f"Total items: {count}")
```

#### `get_absolute_path(item_id)`

Get the file system path for an item.

**Returns**: `str`

#### `get_favorites()`

Get current user's favorite items.

**Returns**: `List[Dict[str, Any]]`

#### `add_to_favorites(item_ids)`

Add items to favorites.

#### `clear_favorites()`

Remove all favorites.

#### `approve_items(item_ids)`

Approve (flag) items.

#### `delete_items(item_ids)`

Delete items from catalog.

---

### TagsAPI

**Access**: `api.tags`

#### `get_all_tags()`

Get complete tag schema with IDs, GUIDs, and types.

**Returns**: `List[TagInfo]`

**Example**:

```python
tags = api.tags.get_all_tags()
for tag in tags:
    print(f"{tag.name} (ID: {tag.id}, GUID: {tag.guid})")
    
# Find specific tag
keywords_tag = next((t for t in tags if t.name == "Keywords"), None)
if keywords_tag:
    print(f"Keywords tag ID: {keywords_tag.id}")
```

#### `get_tag_values(tag_id, parent_value_id, filter_text, page_index, page_size)`

Get values for an indexed tag (e.g., Keywords, Categories).

**Parameters**:
- `tag_id` (int): Tag ID (from `get_all_tags()`)
- `parent_value_id` (int): Parent value ID for hierarchical tags (-2 = all levels)
- `filter_text` (str): Text filter (default: "")
- `page_index` (int): Page index (default: 0)
- `page_size` (int): Items per page (default: 500)

**Returns**: `List[TagValue]`

**Examples**:

```python
# Get all keyword values
keywords = api.tags.get_tag_values(tag_id=13)
for kw in keywords:
    print(f"{kw.text}: {kw.count} items")

# Search for specific keywords
urban_keywords = api.tags.get_tag_values(
    tag_id=13,
    filter_text="urban"
)

# Hierarchical categories (get all levels)
categories = api.tags.get_tag_values(
    tag_id=14,
    parent_value_id=-2
)

# Get children of specific category
subcategories = api.tags.get_tag_values(
    tag_id=14,
    parent_value_id=100  # Parent category ID
)
```

#### `find_tag_values(tag_id, filter_text)`

Search for specific tag values by text.

**Returns**: `List[TagValue]`

**Example**:

```python
matches = api.tags.find_tag_values(
    tag_id=13,
    filter_text="city"
)
```

#### `create_tag_value(tag_guid, value_text, parent_id)`

Create a new tag value.

**Example**:

```python
new_value_id = api.tags.create_tag_value(
    tag_guid="...",
    value_text="New Keyword",
    parent_id=None
)
```

#### `update_tag_value(tag_id, value_id, new_text)`

Update existing tag value text.

#### `delete_tag_value(tag_guid, value_id)`

Delete a tag value.

---

### CollectionsAPI

**Access**: `api.collections`

#### `get_all(index, page_size)`

Get list of shared collections.

**Returns**: `List[SharedCollection]`

**Example**:

```python
collections = api.collections.get_all()
for coll in collections:
    print(f"{coll.name}")
    print(f"  Items: {coll.item_count}")
    print(f"  Code: {coll.code}")
    print(f"  Created: {coll.created}")
```

#### `get_details(collection_id)`

Get detailed information about a collection.

**Returns**: `Dict[str, Any]`

#### `get_items(collection_id, index, page_size)`

Get media items in a collection.

**Returns**: `List[Dict[str, Any]]`

**Example**:

```python
items = api.collections.get_items(
    collection_id=42,
    page_size=200
)
```

#### `create(name, description, item_ids)`

Create a new shared collection.

**Returns**: `int` (collection ID)

**Example**:

```python
new_coll_id = api.collections.create(
    name="Best Photos 2026",
    description="Selected highlights from 2026",
    item_ids=[123, 456, 789]
)
```

#### `update(collection_id, name, description)`

Update collection properties.

#### `delete(collection_ids)`

Delete collections.

**Example**:

```python
api.collections.delete([42, 43, 44])
```

---

### ItemDataAPI

**Access**: `api.item_data`

#### `get(item_id, get_all)`

Get metadata for a media item.

**Parameters**:
- `item_id` (int): Item ID
- `get_all` (bool): If True, get all metadata; if False, get based on property panel (default: False)

**Returns**: `Dict[str, Any]`

**Example**:

```python
# Get metadata based on property panel
metadata = api.item_data.get(item_id=123)

# Get ALL metadata
full_metadata = api.item_data.get(item_id=123, get_all=True)

# Parse metadata
for group in metadata.get('properties', []):
    print(f"\n{group['groupName']}:")
    for prop in group.get('properties', []):
        print(f"  {prop['propertyName']}: {prop.get('propertyValue', '')}")
```

#### `batch_update(item_ids, operations, exclude_ids)`

Batch update tags on multiple items.

**Parameters**:
- `item_ids` (List[int]): Items to update
- `operations` (List[Dict]): Tag operations (see example)
- `exclude_ids` (List[int], optional): Items to exclude

**Example**:

```python
# Add keyword to items
api.item_data.batch_update(
    item_ids=[123, 456, 789],
    operations=[{
        "guid": "keywords-guid-here",
        "id": 4949,  # Keyword value ID
        "remove": False
    }]
)

# Remove tag from items
api.item_data.batch_update(
    item_ids=[123],
    operations=[{
        "guid": "categories-guid-here",
        "id": 200,
        "remove": True
    }]
)

# Update multiple tags at once
api.item_data.batch_update(
    item_ids=[123],
    operations=[
        {"guid": "...", "id": 4949, "remove": False},  # Add keyword
        {"guid": "...", "id": 200, "remove": False},   # Add category
        {"guid": "...", "value": "New description"}    # Set description
    ]
)
```

#### `get_default_layout()`

Get available tags for property panel.

**Returns**: `List[Dict[str, Any]]`

---

### SettingsAPI

**Access**: `api.settings`

#### `get_version()`

Get Daminion server version.

**Returns**: `str`

#### `get_logged_user()`

Get current user information.

**Returns**: `Dict[str, Any]`

#### `get_rights()`

Get current user's permissions.

**Returns**: `Dict[str, bool]`

**Example**:

```python
rights = api.settings.get_rights()
if rights.get('canDelete', False):
    print("User can delete items")
if rights.get('canUpload', False):
    print("User can upload files")
```

#### `get_catalog_guid()`

Get current catalog GUID.

#### `get_export_presets()`

Get available export presets.

---

### ThumbnailsAPI

**Access**: `api.thumbnails`

#### `get(item_id, width, height)`

Get thumbnail image.

**Returns**: `bytes` (JPEG data)

**Example**:

```python
# Get thumbnail
thumbnail = api.thumbnails.get(item_id=123, width=200, height=200)

# Save to file
with open("thumbnail.jpg", "wb") as f:
    f.write(thumbnail)
```

#### `get_preview(item_id, width, height)`

Get larger preview image.

**Example**:

```python
preview = api.thumbnails.get_preview(item_id=123, width=1920, height=1080)
```

---

### DownloadsAPI

**Access**: `api.downloads`

#### `get_original(item_id)`

Download original file.

**Returns**: `bytes`

**Example**:

```python
file_data = api.downloads.get_original(item_id=123)

with open("downloaded_image.jpg", "wb") as f:
    f.write(file_data)
```

#### `get_with_preset(item_id, preset_guid)`

Export file using an export preset.

**Example**:

```python
# Get export presets
presets = api.settings.get_export_presets()
web_preset = next(p for p in presets if p['name'] == "Web JPEG")

# Export with preset
exported = api.downloads.get_with_preset(
    item_id=123,
    preset_guid=web_preset['guid']
)
```

---

### ImportsAPI

**Access**: `api.imports`

#### `get_supported_formats()`

Get list of supported file extensions.

**Returns**: `List[str]`

#### `import_by_urls(file_urls, tags)`

Import files from URLs.

**Returns**: `str` (import session ID)

**Example**:

```python
session_id = api.imports.import_by_urls(
    file_urls=[
        "https://example.com/photo1.jpg",
        "https://example.com/photo2.jpg"
    ],
    tags={"Keywords": ["imported", "web"]}
)
```

---

### UserManagerAPI

**Access**: `api.user_manager`

#### `get_users()`

Get all users (admin only).

#### `get_roles()`

Get all user roles.

#### `create_user(username, password, email, role_id)`

Create new user (admin only).

---

## Common Patterns

### Pattern 1: Find Tag ID by Name

```python
# Get all tags
tags = api.tags.get_all_tags()

# Find Keywords tag
keywords_tag = next((t for t in tags if t.name == "Keywords"), None)

if keywords_tag:
    tag_id = keywords_tag.id
    tag_guid = keywords_tag.guid
    print(f"Keywords: ID={tag_id}, GUID={tag_guid}")
```

### Pattern 2: Search Items by Keyword

```python
# Step 1: Get tag schema
tags = api.tags.get_all_tags()
keywords_tag = next(t for t in tags if t.name == "Keywords")

# Step 2: Find keyword value
keyword_values = api.tags.find_tag_values(
    tag_id=keywords_tag.id,
    filter_text="city"
)

if keyword_values:
    value_id = keyword_values[0].id
    
    # Step 3: Search items with that keyword
    items = api.media_items.search(
        query_line=f"{keywords_tag.id},{value_id}",
        operators=f"{keywords_tag.id},any"
    )
    
    print(f"Found {len(items)} items with keyword 'city'")
```

### Pattern 3: Batch Tag Items from Search Results

```python
# Search for items
items = api.media_items.search(query="landscape")
item_ids = [item['id'] for item in items]

# Get tag schema
tags = api.tags.get_all_tags()
categories_tag = next(t for t in tags if t.name == "Categories")

# Find category value
category_values = api.tags.find_tag_values(
    tag_id=categories_tag.id,
    filter_text="Nature"
)
nature_category_id = category_values[0].id

# Apply category to all items
api.item_data.batch_update(
    item_ids=item_ids,
    operations=[{
        "guid": categories_tag.guid,
        "id": nature_category_id,
        "remove": False
    }]
)

print(f"Tagged {len(item_ids)} items with 'Nature' category")
```

### Pattern 4: Process All Collection Items

```python
# Get all collections
collections = api.collections.get_all()

for coll in collections:
    print(f"\nProcessing collection: {coll.name}")
    
    # Get items in batches
    page_size = 200
    index = 0
    
    while True:
        items = api.collections.get_items(
            collection_id=coll.id,
            index=index,
            page_size=page_size
        )
        
        if not items:
            break
        
        # Process items
        for item in items:
            print(f"  - {item['filename']}")
        
        index += page_size
```

### Pattern 5: Export All Search Results

```python
import os

# Search for items
items = api.media_items.search(query="portfolio")

# Create download directory
os.makedirs("downloads", exist_ok=True)

# Download each item
for i, item in enumerate(items, 1):
    print(f"Downloading {i}/{len(items)}: {item['filename']}")
    
    file_data = api.downloads.get_original(item['id'])
    
    filepath = os.path.join("downloads", item['filename'])
    with open(filepath, "wb") as f:
        f.write(file_data)

print(f"Downloaded {len(items)} files")
```

---

## Error Handling

### Exception Hierarchy

```
DaminionAPIError (base)
├── DaminionAuthenticationError    # 401 Unauthorized
├── DaminionPermissionError        # 403 Forbidden
├── DaminionNotFoundError          # 404 Not Found
├── DaminionRateLimitError         # 429 Too Many Requests
└── DaminionNetworkError           # Network/connection errors
```

### Handling Errors

```python
from src.core.daminion_api import (
    DaminionAPI,
    DaminionAuthenticationError,
    DaminionNotFoundError,
    DaminionAPIError
)

try:
    with DaminionAPI(url, user, password) as api:
        items = api.media_items.search(query="test")
        
except DaminionAuthenticationError:
    print("Login failed - check credentials")
    
except DaminionNotFoundError as e:
    print(f"Resource not found: {e}")
    
except DaminionAPIError as e:
    print(f"API error: {e}")
    
except Exception as e:
    print(f"Unexpected error: {e}")
```

### Retry Logic Example

```python
import time

def search_with_retry(api, query, max_retries=3):
    """Search with automatic retry on rate limit."""
    for attempt in range(max_retries):
        try:
            return api.media_items.search(query=query)
            
        except DaminionRateLimitError:
            if attempt < max_retries - 1:
                wait_time = 2 ** attempt  # Exponential backoff
                print(f"Rate limited, waiting {wait_time}s...")
                time.sleep(wait_time)
            else:
                raise
```

---

## Best Practices

### 1. Use Context Manager

✅ **Good**:
```python
with DaminionAPI(url, user, password) as api:
    items = api.media_items.search(query="test")
```

❌ **Avoid**:
```python
api = DaminionAPI(url, user, password)
api.authenticate()
items = api.media_items.search(query="test")
# Forgot to call api.logout()!
```

### 2. Cache Tag Schema

Tag IDs don't change during a session, so cache them:

```python
with DaminionAPI(url, user, password) as api:
    # Get tags once at startup
    tags = api.tags.get_all_tags()
    tag_map = {tag.name: tag for tag in tags}
    
    # Reuse throughout session
    keywords_id = tag_map["Keywords"].id
    categories_id = tag_map["Categories"].id
    
    # Now do your work
    items = api.media_items.search(...)
```

### 3. Use Pagination for Large Results

```python
def get_all_items(api, query):
    """Get all items, handling pagination."""
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
        
        # Optional: limit for safety
        if index > 10000:
            print("Warning: Reached 10k item limit")
            break
    
    return all_items
```

### 4. Batch Operations

Instead of updating items one-by-one:

✅ **Good** (batch update):
```python
api.item_data.batch_update(
    item_ids=[1, 2, 3, 4, 5],
    operations=[{"guid": "...", "id": 123}]
)
```

❌ **Slow** (individual updates):
```python
for item_id in [1, 2, 3, 4, 5]:
    api.item_data.batch_update(
        item_ids=[item_id],
        operations=[{"guid": "...", "id": 123}]
    )
```

### 5. Configure Rate Limiting

Adjust based on your server capacity:

```python
# Conservative (slower but safer)
api = DaminionAPI(url, user, password, rate_limit=0.5)  # 500ms between calls

# Aggressive (faster but may hit limits)
api = DaminionAPI(url, user, password, rate_limit=0.05)  # 50ms between calls

# Disabled (use with caution!)
api = DaminionAPI(url, user, password, rate_limit=0)
```

### 6. Handle Large Catalogs Carefully

```python
# Don't do this for 1M+ item catalogs!
# all_items = api.media_items.search(page_size=999999)

# Instead: use specific queries or pagination
items = api.media_items.search(
    query="flag:approved",  # Narrow down first
    page_size=500
)
```

---

## Migration Guide

### From Old `daminion_client.py` to New `daminion_api.py`

#### Authentication

**Old**:
```python
client = DaminionClient(url, user, password)
client.authenticate()
```

**New**:
```python
with DaminionAPI(url, user, password) as api:
    # Auto-authenticated
    pass
```

#### Search

**Old**:
```python
items = client.search_items(
    query_line="13,4949",
    operators="13,any"
)
```

**New**:
```python
items = api.media_items.search(
    query_line="13,4949",
    operators="13,any"
)
```

#### Get Tag Values

**Old**:
```python
values = client.get_tag_values(tag_name="Keywords")
```

**New**:
```python
# First get tag ID
tags = api.tags.get_all_tags()
keywords_tag = next(t for t in tags if t.name == "Keywords")

# Then get values
values = api.tags.get_tag_values(tag_id=keywords_tag.id)
```

#### Collections

**Old**:
```python
collections = client.get_shared_collections()
```

**New**:
```python
collections = api.collections.get_all()
```

#### Item Metadata

**Old**:
```python
metadata = client.get_item_details(item_id)
```

**New**:
```python
metadata = api.item_data.get(item_id, get_all=True)
```

---

## Appendix: Common Tag IDs

These are **typical** IDs but may vary by installation. Always use `api.tags.get_all_tags()` to verify.

| Tag Name | Typical ID (int) | Typical ID (indexed) |
|----------|------------------|----------------------|
| Keywords | 13 | 5000 |
| Categories | 14 | 5001 |
| Description | 67 | - |
| Rating | 64 | - |
| Flag | 42 | - |
| Color Label | 74 | - |
| Collections | 41 | - |
| Shared Collections | 46 | - |
| People | 17 | 5004 |
| Location | 18 | 5005 |

---

## Support

- **Official API Docs**: https://marketing.daminion.net/APIHelp
- **Daminion Forum**: https://daminion.net/forum
- **Project Issues**: Report bugs to the Synapic project team

---

**End of Documentation**
