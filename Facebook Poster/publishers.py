import abc
import tweepy
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

class TwitterPublisher(Publisher):
    def __init__(self):
        self.api_key = Config.X_API_KEY
        self.api_key_secret = Config.X_API_SECRET
        self.access_token = Config.X_ACCESS_TOKEN
        self.access_token_secret = Config.X_ACCESS_TOKEN_SECRET
        
        if all([self.api_key, self.api_key_secret, self.access_token, self.access_token_secret]):
            try:
                # Client for posting Tweets (v2)
                self.client = tweepy.Client(
                    consumer_key=self.api_key,
                    consumer_secret=self.api_key_secret,
                    access_token=self.access_token,
                    access_token_secret=self.access_token_secret
                )
                # API v1.1 for media upload
                auth = tweepy.OAuth1UserHandler(
                    self.api_key, self.api_key_secret,
                    self.access_token, self.access_token_secret
                )
                self.api = tweepy.API(auth)
                self.enabled = True
            except Exception as e:
                print(f"Error initializing Twitter client: {e}")
                self.enabled = False
        else:
            self.enabled = False

    def post(self, text, image_url=None, **kwargs):
        if not self.enabled:
            print("Twitter not configured or disabled.")
            return None

        print("Posting to Twitter...")
        try:
            media_id = None
            if image_url:
                # Need to download image first
                response = requests.get(image_url, stream=True)
                if response.status_code == 200:
                    with tempfile.NamedTemporaryFile(suffix=".jpg", delete=False) as temp:
                        for chunk in response.iter_content(1024):
                            temp.write(chunk)
                        temp_path = temp.name
                    
                    try:
                        media = self.api.media_upload(filename=temp_path)
                        media_id = media.media_id
                    finally:
                        os.remove(temp_path)
            
            # Create Tweet
            response = self.client.create_tweet(text=text, media_ids=[media_id] if media_id else None)
            print(f"Successfully posted to Twitter! ID: {response.data['id']}")
            return response.data['id']
            
        except Exception as e:
            print(f"Error posting to Twitter: {e}")
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
        else:
            self.enabled = False

    def post(self, text, image_url=None, title=None, **kwargs):
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
            if image_url:
                # Upload Image
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
                featured_media_id = media_res.json().get("id", 0)

            # Create Post
            post_data = {
                "title": title,
                "content": text, # In WP this is HTML content
                "status": "publish",
                "featured_media": featured_media_id
            }
            
            post_res = requests.post(
                f"{self.api_url}/posts",
                json=post_data,
                auth=auth
            )
            post_res.raise_for_status()
            result = post_res.json()
            print(f"Successfully posted to WordPress! ID: {result.get('id')}")
            return result.get('id')

        except Exception as e:
            print(f"Error posting to WordPress: {e}")
            if 'post_res' in locals():
                print(f"WP Response: {post_res.text}")
            return None
