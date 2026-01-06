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

    def generate_seo_article(self, topic):
        """Generates a detailed, SEO-optimized blog post for WordPress (JSON bundle)."""
        print(f"Generating SEO article for topic: {topic}")
        
        system_prompt = (
            "You are an expert SEO content writer. "
            "Generate a JSON object containing the following fields:\n"
            "1. 'html_content': A comprehensive blog post (min 600 words). Use HTML (<h3>, <p>, <ul>). NO <h1> (title is separate). \n"
            "2. 'meta_title': An optimized 60 char title.\n"
            "3. 'meta_description': A 150-160 char engaging description.\n"
            "4. 'image_alt_text': SEO-optimized alt text for an image about this topic.\n"
            "Output ONLY valid JSON."
        )
        
        import json
        
        result = self._call_llm(system_prompt, topic)

        # Clean up markdown code blocks if present
        clean_result = result
        if "```" in clean_result:
            clean_result = clean_result.replace("```json", "").replace("```", "").strip()
            
        try:
            return json.loads(clean_result)
        except Exception as e:
            print(f"Error parsing JSON from LLM: {e}")
            print(f"Raw Result: {result}") # Debug
            # Fallback for plain text if LLM fails JSON instruction
            return {
                "html_content": result, # We might still have artifacts here if we failed to parse, but stripping helps chances of parsing.
                "meta_title": topic,
                "meta_description": result[:160] if result else "",
                "image_alt_text": topic
            }

    def generate_social_summary(self, topic):
        """Generates a short, punchy social media summary."""
        # ... (same as before) ...
        print(f"Generating social summary for topic: {topic}")
        
        system_prompt = (
            "You are a social media manager. "
            "Write a short, engaging, and conversational hook about the following topic. "
            "It should serve as a teaser to click a link to read more. "
            "Include 1-2 relevant hashtags. "
            "Max 100 words. "
            "Output ONLY the text."
        )
        
        return self._call_llm(system_prompt, topic)

    def _call_llm(self, system_prompt, topic):
        # ... (same as before) ...
        try:
            response = requests.post(
                url="https://openrouter.ai/api/v1/chat/completions",
                headers={
                    "Authorization": f"Bearer {self.openrouter_api_key}",
                    "HTTP-Referer": "http://localhost:5000",
                    "X-Title": "Facebook Auto Poster",
                    "Content-Type": "application/json"
                },
                json={
                    "model": self.model_id,
                    "messages": [
                        {"role": "system", "content": system_prompt},
                        {"role": "user", "content": f"Topic: {topic}\nContent:"}
                    ],
                    "temperature": 0.7,
                }
            )
            response.raise_for_status()
            data = response.json()
            return data['choices'][0]['message']['content'].strip()
        except Exception as e:
            print(f"Error generating text: {e}")
            return None

    def get_image_url(self, topic):
        # ... (same as before) ...
        print(f"Fetching image for topic: {topic}")
        
        url = "https://api.pexels.com/v1/search"
        headers = {"Authorization": self.pexels_api_key}
        params = {"query": topic, "per_page": 1, "orientation": "landscape"}
        
        try:
            response = requests.get(url, headers=headers, params=params)
            response.raise_for_status()
            data = response.json()
            
            if data['photos']:
                return data['photos'][0]['src']['large2x']
            else:
                print("No photos found on Pexels.")
                return None
        except Exception as e:
            print(f"Error fetching image: {e}")
            return None

    def publish_content(self, topic, article_content, social_summary, image_url, fb_page_id=None, fb_access_token=None, schedule_time=None, meta_description=None, alt_text=None):
        """Publishes content to WordPress then Facebook."""
        results = {}
        from publishers import FacebookPublisher, WordPressPublisher

        # 1. WordPress (Primary source)
        # We publish/schedule to WP first to get the link
        wp_link = None
        wp = WordPressPublisher()
        if wp.enabled:
            wp_result = wp.post(
                text=article_content, 
                image_url=image_url, 
                title=topic, 
                schedule_time=schedule_time,
                meta_description=meta_description,
                alt_text=alt_text
            )
            if wp_result:
                results['wordpress'] = wp_result.get('id')
                wp_link = wp_result.get('link')
        
        # 2. Facebook
        if fb_page_id and fb_access_token:
            fb = FacebookPublisher(fb_page_id, fb_access_token)
            
            # Construct FB message
            fb_message = social_summary
            if wp_link:
                fb_message += f"\n\nRead more: {wp_link}"
            
            fb_id = fb.post(fb_message, image_url, schedule_time=schedule_time)
            results['facebook'] = fb_id

        return results

    # Legacy wrapper for backward compatibility if needed, but we will update caller
    def create_post(self, page_id, page_access_token, message, image_url=None, schedule_time=None):
        return self.publish_content(
            topic="Unknown Topic", 
            article_content=message, # Treat message as both if generic
            social_summary=message,
            image_url=image_url, 
            fb_page_id=page_id, 
            fb_access_token=page_access_token, 
            schedule_time=schedule_time
        ).get('facebook')

if __name__ == "__main__":
    # Test
    ce = ContentEngine()
    # topic = "The importance of critical thinking"
    # text = ce.generate_article(topic)
    # print(f"Generated Article:\n{text}")
    # img = ce.get_image_url(topic)
    # print(f"Image URL: {img}")
