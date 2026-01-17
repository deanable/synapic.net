import abc

import requests
import os
import tempfile
from config import Config
import base64

class Publisher(abc.ABC):
    @abc.abstractmethod
    def post(self, text, image_url=None, **kwargs):
        pass

class FacebookPublisher(Publisher):
    def __init__(self, page_id, access_token):
        self.page_id = page_id
        self.access_token = access_token
        self.base_url = "https://graph.facebook.com/v18.0"

    def post(self, text, image_url=None, schedule_time=None):
        print(f"Posting to FB Page ID {self.page_id}...")
        
        payload = {
            "access_token": self.access_token
        }

        if schedule_time:
            payload["published"] = "false"
            payload["scheduled_publish_time"] = schedule_time
        
        if image_url:
            # Post photo
            url = f"{self.base_url}/{self.page_id}/photos"
            payload["url"] = image_url
            payload["caption"] = text
        else:
            # Post text only
            url = f"{self.base_url}/{self.page_id}/feed"
            payload["message"] = text
            
        try:
            response = requests.post(url, data=payload)
            response.raise_for_status()
            result = response.json()
            print(f"Successfully posted/scheduled to Facebook! ID: {result.get('id')}")
            return result.get('id')
        except Exception as e:
            print(f"Error posting to Facebook: {e}")
            if 'response' in locals() and hasattr(response, 'text'):
                print(f"FB Response: {response.text}")
            return None



class WordPressPublisher(Publisher):
    def __init__(self):
        self.url = Config.WORDPRESS_URL
        self.user = Config.WORDPRESS_USER
        self.password = Config.WORDPRESS_APP_PASSWORD
        
        if self.url and self.user and self.password:
            self.enabled = True
            # Ensure URL ends with wp-json/wp/v2
            if not self.url.endswith("/wp-json/wp/v2"):
                 # Handle cases like "http://site.com" -> "http://site.com/wp-json/wp/v2"
                 # or "http://site.com/"
                 base = self.url.rstrip("/")
                 self.api_url = f"{base}/wp-json/wp/v2"
            else:
                self.api_url = self.url
            self.auth = (self.user, self.password)
        else:
            self.enabled = False
            self.auth = None

    def _get_or_create_category(self, category_name):
        """Get category ID by name, create if doesn't exist."""
        if not self.enabled:
            return None
        
        try:
            # First, try to find existing category
            response = requests.get(
                f"{self.api_url}/categories",
                params={"search": category_name},
                auth=self.auth
            )
            response.raise_for_status()
            categories = response.json()
            
            # Check if exact match exists
            for cat in categories:
                if cat.get("name", "").lower() == category_name.lower():
                    print(f"Found existing category '{category_name}' (ID: {cat['id']})")
                    return cat["id"]
            
            # Category doesn't exist, create it
            create_response = requests.post(
                f"{self.api_url}/categories",
                json={"name": category_name},
                auth=self.auth
            )
            create_response.raise_for_status()
            new_cat = create_response.json()
            print(f"Created new category '{category_name}' (ID: {new_cat['id']})")
            return new_cat["id"]
            
        except Exception as e:
            print(f"Error getting/creating category '{category_name}': {e}")
            return None

    def _get_or_create_tag(self, tag_name):
        """Get tag ID by name, create if doesn't exist."""
        if not self.enabled:
            return None
        
        try:
            # First, try to find existing tag
            response = requests.get(
                f"{self.api_url}/tags",
                params={"search": tag_name},
                auth=self.auth
            )
            response.raise_for_status()
            tags = response.json()
            
            # Check if exact match exists
            for tag in tags:
                if tag.get("name", "").lower() == tag_name.lower():
                    return tag["id"]
            
            # Tag doesn't exist, create it
            create_response = requests.post(
                f"{self.api_url}/tags",
                json={"name": tag_name},
                auth=self.auth
            )
            create_response.raise_for_status()
            new_tag = create_response.json()
            return new_tag["id"]
            
        except Exception as e:
            print(f"Error getting/creating tag '{tag_name}': {e}")
            return None

    def post(self, text, image_url=None, title=None, schedule_time=None, meta_description=None, alt_text=None, category_name=None, tags_str=None, **kwargs):
        if not self.enabled:
            print("WordPress not configured.")
            return None
            
        print("Posting to WordPress...")
        
        # Use Topic as title if not provided, or just truncate text
        if not title:
            title = text[:50] + "..." if len(text) > 50 else text

        auth = (self.user, self.password)
        
        try:
            featured_media_id = 0
            attachment_url = None
            
            if image_url:
                # 1. Upload Image
                img_data = requests.get(image_url).content
                filename = "post_image.jpg"
                
                headers = {
                    "Content-Type": "image/jpeg",
                    "Content-Disposition": f"attachment; filename={filename}"
                }
                
                media_res = requests.post(
                    f"{self.api_url}/media",
                    data=img_data,
                    headers=headers,
                    auth=auth
                )
                media_res.raise_for_status()
                media_data = media_res.json()
                featured_media_id = media_data.get("id", 0)
                attachment_url = media_data.get("source_url")

                # 2. Update Alt Text if provided
                if alt_text and featured_media_id:
                    try:
                        requests.post(
                            f"{self.api_url}/media/{featured_media_id}",
                            json={"alt_text": alt_text},
                            auth=auth
                        )
                    except Exception as e:
                        print(f"Failed to update alt text: {e}")

            # 3. Inject Image into Content (after first paragraph)
            final_content = text
            if attachment_url and featured_media_id:
                # Construct Image HTML
                # Using standard WP classes helps with styling
                img_html = (
                    f'\n<div class="wp-block-image">'
                    f'<figure class="aligncenter size-large">'
                    f'<img src="{attachment_url}" alt="{alt_text if alt_text else ""}" class="wp-image-{featured_media_id}"/>'
                    f'</figure></div>\n'
                )
                
                # Simple injection: replace first closing p tag
                if "</p>" in final_content:
                    final_content = final_content.replace("</p>", f"</p>{img_html}", 1)
                else:
                    # Fallback: Prepend
                    final_content = img_html + final_content

            # 4. Process Categories and Tags
            category_ids = []
            tag_ids = []
            
            # Get/create category (default to "articles" if not provided)
            if category_name:
                cat_id = self._get_or_create_category(category_name)
                if cat_id:
                    category_ids.append(cat_id)
            else:
                # Default category
                cat_id = self._get_or_create_category("articles")
                if cat_id:
                    category_ids.append(cat_id)
            
            # Get/create tags
            if tags_str:
                tag_names = [tag.strip() for tag in tags_str.split(",") if tag.strip()]
                for tag_name in tag_names:
                    tag_id = self._get_or_create_tag(tag_name)
                    if tag_id:
                        tag_ids.append(tag_id)

            # 5. Create Post
            post_data = {
                "title": title,
                "content": final_content, 
                "status": "publish",
                "featured_media": featured_media_id
            }
            
            # Add categories and tags
            if category_ids:
                post_data["categories"] = category_ids
            if tag_ids:
                post_data["tags"] = tag_ids
            
            # Add Excerpt (Meta Description)
            if meta_description:
                post_data["excerpt"] = meta_description

            if schedule_time:
                # WP expects ISO 8601
                import datetime
                dt = datetime.datetime.fromtimestamp(schedule_time)
                post_data["date"] = dt.isoformat()
                post_data["status"] = "future"
            
            post_res = requests.post(
                f"{self.api_url}/posts",
                json=post_data,
                auth=auth
            )
            post_res.raise_for_status()
            result = post_res.json()
            print(f"Successfully posted to WordPress! ID: {result.get('id')}")
            return {'id': result.get('id'), 'link': result.get('link')}

        except Exception as e:
            print(f"Error posting to WordPress: {e}")
            if 'post_res' in locals():
                print(f"WP Response: {post_res.text}")
            return None
