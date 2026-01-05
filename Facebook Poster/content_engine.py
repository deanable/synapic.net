import requests

from config import Config
import random

class ContentEngine:
    def __init__(self, model_id=None):
        self.openrouter_api_key = Config.OPENROUTER_API_KEY
        self.pexels_api_key = Config.PEXELS_API_KEY
        # Use provided model_id or fallback to a known free one
        self.model_id = model_id if model_id else "meta-llama/llama-3.1-8b-instruct:free"

    @staticmethod
    def get_available_free_models(api_key):
        """Fetches available models from OpenRouter and filters for free ones."""
        try:
            response = requests.get(
                "https://openrouter.ai/api/v1/models",
                headers={"Authorization": f"Bearer {api_key}"}
            )
            response.raise_for_status()
            data = response.json().get("data", [])
            
            # Filter for models ending in ':free' and return valid list of dicts {id, name}
            free_models = [
                {"id": m["id"], "name": m["name"]} 
                for m in data 
                if m["id"].endswith(":free")
            ]
            # Sort alphabetically by name
            free_models.sort(key=lambda x: x["name"])
            return free_models
        except Exception as e:
            print(f"Error fetching models: {e}")
            return []

    def generate_article(self, topic):
        """Generates a short, engaging Facebook post about the topic using OpenRouter."""
        print(f"Generating content for topic: {topic}")
        
        system_prompt = (
            "You are a social media manager for an intellectually curious Facebook Page. "
            "Write a short, engaging, and conversational post about the following topic. "
            "Include 1-2 relevant hashtags. Do not use emojis excessively. "
            "Keep it under 200 words. "
            "Output ONLY the post text."
        )
        
        try:
            response = requests.post(
                url="https://openrouter.ai/api/v1/chat/completions",
                headers={
                    "Authorization": f"Bearer {self.openrouter_api_key}",
                    "HTTP-Referer": "http://localhost:5000", # Ranking/stats
                    "X-Title": "Facebook Auto Poster",
                    "Content-Type": "application/json"
                },
                json={
                    "model": self.model_id,
                    "messages": [
                        {"role": "system", "content": system_prompt},
                        {"role": "user", "content": f"Topic: {topic}\nPost:"}
                    ],
                    "temperature": 0.7,
                    "max_tokens": 300
                }
            )
            response.raise_for_status()
            data = response.json()
            return data['choices'][0]['message']['content'].strip()
            
        except Exception as e:
            print(f"Error generating text: {e}")
            if 'response' in locals() and hasattr(response, 'text'):
                 print(f"Response body: {response.text}")
            return None

    def get_image_url(self, topic):
        """Fetches a high-quality image URL from Pexels based on the topic."""
        print(f"Fetching image for topic: {topic}")
        
        # Sometimes the topic is too abstract. 
        # Ideally we'd ask LLM for a visual search term, but let's try direct first.
        
        url = "https://api.pexels.com/v1/search"
        headers = {"Authorization": self.pexels_api_key}
        params = {"query": topic, "per_page": 1, "orientation": "landscape"}
        
        try:
            response = requests.get(url, headers=headers, params=params)
            response.raise_for_status()
            data = response.json()
            
            if data['photos']:
                # Get the 'large2x' or 'original' for quality
                return data['photos'][0]['src']['large2x']
            else:
                print("No photos found on Pexels.")
                return None
        except Exception as e:
            print(f"Error fetching image: {e}")
            return None

    def create_post(self, page_id, page_access_token, message, image_url=None, schedule_time=None):
        """Publishes the post to the Facebook Page.
        
        Args:
            schedule_time (int): Unix timestamp for when to publish. Must be between 10 mins and 6 months from now.
        """
        print(f"Posting to Page ID {page_id}...")
        
        payload = {
            "access_token": page_access_token
        }

        if schedule_time:
            payload["published"] = "false"
            payload["scheduled_publish_time"] = schedule_time
        
        if image_url:
            # Post photo
            url = f"https://graph.facebook.com/v18.0/{page_id}/photos"
            payload["url"] = image_url
            payload["caption"] = message
        else:
            # Post text only
            url = f"https://graph.facebook.com/v18.0/{page_id}/feed"
            payload["message"] = message
            
        try:
            response = requests.post(url, data=payload)
            response.raise_for_status()
            result = response.json()
            print(f"Successfully {'scheduled' if schedule_time else 'posted'}! ID: {result.get('id')}")
            return result.get('id')
        except Exception as e:
            print(f"Error posting to Facebook: {response.text if 'response' in locals() else e}")
            raise e

if __name__ == "__main__":
    # Test
    ce = ContentEngine()
    # topic = "The importance of critical thinking"
    # text = ce.generate_article(topic)
    # print(f"Generated Article:\n{text}")
    # img = ce.get_image_url(topic)
    # print(f"Image URL: {img}")
