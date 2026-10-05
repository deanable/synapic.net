# Daminion API Investigation & Debugging Report

This document summarizes the findings and technical solutions developed during the debugging of the Daminion API integration for "Shared Collections" and "Saved Searches".

## 1. Environment Details
- **Target Server**: `http://researchserver.juicefilm.local/daminion`
- **Scope**: Integration with `Synapic` Python client (`DaminionClient`).
- **Core Challenge**: Standard API endpoints for specific scopes (Shared Collections, Saved Searches) were returning empty results or 404/500 errors despite data existing on the server.

## 2. Key Findings: Shared Collections (Tag ID 45)

### The "Empty Result" Bug
- **Symptom**: Calling `/api/SharedCollection/GetItems?id=11` or `/api/MediaItems/Get?query=45,11` returns a response with `totalCount: 200` but an empty `mediaItems: []` list.
- **Root Cause**: The specific Daminion server version/configuration appears to restrict item lists for certain shared collections in the primary Get endpoints, even for authenticated administrative users.
- **Verification**: Direct retrieval via `/api/MediaItems/GetByIds?ids=1,2,3...` for known IDs *succeeded*, proving the items are accessible if their IDs are known.

### Solution / Fallback Logic
1. **Multi-Syntax Querying**: The client now tries both `query=45:11` and `query=45,11`.
2. **AccessCode Retrieval**: Since internal IDs often fail in public-facing endpoints, the client now attempts to fetch the alphanumeric `accessCode` (e.g., `pjrzp5ny`) from `/api/SharedCollection/GetDetails/{id}` and retries using `/api/SharedCollection/PublicItems?code={accessCode}`.
3. **Small-Catalog Brute Force**: For catalogs with fewer than 1,000 items, if the search returns 0 items but `totalCount > 0`, the client performs a range scan (fetching all IDs) and filters them locally.

## 3. Key Findings: Saved Searches (Tag ID 39)

### Endpoint Inconsistency
- **Symptom**: Common endpoints like `/api/MediaItems/GetSavedSearches` or specialized `SavedSearches/Get` consistently returned **404 Not Found**.
- **Discovery**: Saved Searches are stored as a taxonomy tree under **Tag ID 39**.

### Solution / Fallback Logic
- **Taxonomy Discovery**: If specialized endpoints fail, the client falls back to `get_tag_values("Saved Searches")`, which traverses the tag tree to find the available search names and their associated Value IDs.
- **Query Format**: Items for a saved search are retrieved using the structured query syntax `query=39,{valueID}`.

## 4. General API Implementation Notes

### URL Encoding
- **Quirk**: The Daminion API is sensitive to control characters. Search strings containing spaces (e.g., `Hatfield Station.jpg`) cause `http.client.InvalidURL` exceptions in Python's `urllib` if not strictly quoted.
- **Fix**: All search parameters are now wrapped in `urllib.parse.quote()`.

### Dynamic Tag ID Mapping
- **Discovery**: While Tag 39 and 45 are common, they are not guaranteed.
- **Fix**: The client now calls `/api/settings/getTags` during authentication to build a dynamic `_tag_id_map`. It specifically looks for "Saved Searches" and "Shared Collections" to update internal IDs.

### Response Parsing
- **Quirk**: Daminion responses vary wildly between internal versions (list of objects vs. object with `mediaItems` vs. object with `data` vs. object with `items`).
- **Fix**: The `_make_request` helper and dedicated retrieval methods now use a "greedy" search for list objects in the response JSON.

## 5. Summary of `DaminionClient` Enhancements
- Added `SAVED_SEARCH_TAG_ID` and `SHARED_COLLECTIONS_TAG_ID` dynamic discovery.
- Implemented `get_items_by_query` for structured requests.
- Added `scan_catalog` for brute-force fallbacks on small datasets.
- Improved `_passes_filters` to handle client-side filtering when server-side fails.
- Standardized ID normalization (checking `id` vs `uniqueId`).
