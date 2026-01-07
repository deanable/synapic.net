from openai import OpenAI
import config
import json

class AIOptimizer:
    def __init__(self):
        self.client = OpenAI(
            base_url=config.OPENROUTER_BASE_URL,
            api_key=config.OPENROUTER_API_KEY,
        )

    def generate_optimization(self, title, description, transcript):
        """Generates optimized titles, description, and chapters."""
        
        prompt = f"""
        You are an expert YouTube strategist and copywriter. I need you to optimize a YouTube video based on its current details and transcript.

        Here is the video information:
        Current Title: {title}
        Current Description: {description}
        Transcript: {transcript[:15000]}... (truncated if too long)

        Please provide the following:
        1. 5 Clickable, SEO-optimized Titles.
        2. An optimized Video Description (including a hook, summary, and key points).
        3. YouTube Chapters with timestamps (00:00 format) and engaging titles.

        Return the response in JSON format with the following keys:
        - optimized_titles: list of strings
        - optimized_description: string
        - chapters: list of objects {{ "timestamp": "string", "title": "string" }}
        """

        try:
            response = self.client.chat.completions.create(
                model=config.MODEL_NAME,
                messages=[
                    {"role": "system", "content": "You are a helpful YouTube optimization assistant. Output JSON only."},
                    {"role": "user", "content": prompt}
                ],
                response_format={"type": "json_object"} 
            )
            
            content = response.choices[0].message.content
            return json.loads(content)
        except Exception as e:
            print(f"Error calling AI: {e}")
            return None
