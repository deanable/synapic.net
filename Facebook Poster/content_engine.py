import requests
from huggingface_hub import InferenceClient
from config import Config
import random

class ContentEngine:
    def __init__(self):
        self.hf_client = InferenceClient(token=Config.HF_TOKEN)
        self.pexels_api_key = Config.PEXELS_API_KEY
        # Mistral-7B or Llama-3-8b are good choices. 
        # Using mistralai/Mistral-7B-Instruct-v0.2 for now as it's reliable.
        self.model_id = "mistralai/Mistral-7B-Instruct-v0.2"

    def generate_article(self, topic):
        """Generates a short, engaging Facebook post about the topic."""
        print(f"Generating content for topic: {topic}")
        
        system_prompt = (
            "You are a social media manager for an intellectually curious Facebook Page. "
            "Write a short, engaging, and conversational post about the following topic. "
            "Include 1-2 relevant hashtags. Do not use emojis excessively. "
            "Keep it under 200 words. "
            "Output ONLY the post text."
        )
        
        prompt = f"Topic: {topic}\nPost:"
        
        messages = [
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": prompt}
        ]

        try:
            # Using chat_completion for better structure if available, 
            # or simple text_generation. InferenceClient adapts.
            # For Mistral Instruct, straight generation with refined prompt is often safer 
            # if chat templating isn't perfectly handled by the API wrapper.
            # But let's try the modern chat method first.
            
            # Note: For free API, sometimes simple text generation is more stable. 
            # Let's use text_generation with a formatted prompt.
            full_prompt = f"<s>[INST] {system_prompt}\n\n{prompt} [/INST]"
            
            response = self.hf_client.text_generation(
                full_prompt, 
                model=self.model_id, 
                max_new_tokens=300,
                temperature=0.7
            )
            return response.strip()
        except Exception as e:
            print(f"Error generating text: {e}")
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

    def create_post(self, page_id, page_access_token, message, image_url=None):
        """Publishes the post to the Facebook Page."""
        print(f"Posting to Page ID {page_id}...")
        
        if image_url:
            # Post photo
            url = f"https://graph.facebook.com/v18.0/{page_id}/photos"
            payload = {
                "url": image_url,
                "caption": message,
                "access_token": page_access_token
            }
        else:
            # Post text only
            url = f"https://graph.facebook.com/v18.0/{page_id}/feed"
            payload = {
                "message": message,
                "access_token": page_access_token
            }
            
        try:
            response = requests.post(url, data=payload)
            response.raise_for_status()
            result = response.json()
            print(f"Successfully posted! ID: {result.get('id')}")
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
