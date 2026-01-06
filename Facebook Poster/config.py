import os
from dotenv import load_dotenv

load_dotenv()

class Config:
    FB_APP_ID = os.getenv("FB_APP_ID")
    FB_APP_SECRET = os.getenv("FB_APP_SECRET")
    OPENROUTER_API_KEY = os.getenv("OPENROUTER_API_KEY")
    PEXELS_API_KEY = os.getenv("PEXELS_API_KEY")
    REDIRECT_URI = 'http://localhost:5000/callback' # Common for local testing



    # WordPress
    WORDPRESS_URL = os.getenv("WORDPRESS_URL")
    WORDPRESS_USER = os.getenv("WORDPRESS_USER")
    WORDPRESS_APP_PASSWORD = os.getenv("WORDPRESS_APP_PASSWORD")

    @staticmethod
    def validate():
        missing = []
        # Required for Core
        if not Config.OPENROUTER_API_KEY: missing.append("OPENROUTER_API_KEY")
        # Pexels is used for image generation
        if not Config.PEXELS_API_KEY: missing.append("PEXELS_API_KEY")
        
        # We don't enforce all keys because user might only want some integrations
        # But let's at least warn or just pass. 
        # The original code enforced FB keys. We should probably relax this if we want to allow "X only" mode.
        # However, for now, let's keep FB required if we don't want to break existing logic too much, 
        # OR better: check if at least ONE platform is configured?
        # For this step, I'll remove strict validation for FB if I'm making it multi-platform, 
        # but the user didn't ask to remove FB. 
        # Let's keep FB required for now to minimize regression, 
        # but I'll add a comment that we might want to make them optional later.
        
        if not Config.FB_APP_ID: missing.append("FB_APP_ID")
        if not Config.FB_APP_SECRET: missing.append("FB_APP_SECRET")
        
        if missing:
            raise ValueError(f"Missing environment variables: {', '.join(missing)}")
