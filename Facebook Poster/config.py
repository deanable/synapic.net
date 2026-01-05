import os
from dotenv import load_dotenv

load_dotenv()

class Config:
    FB_APP_ID = os.getenv("FB_APP_ID")
    FB_APP_SECRET = os.getenv("FB_APP_SECRET")
    OPENROUTER_API_KEY = os.getenv("OPENROUTER_API_KEY")
    PEXELS_API_KEY = os.getenv("PEXELS_API_KEY")
    REDIRECT_URI = 'http://localhost:5000/callback' # Common for local testing
    
    @staticmethod
    def validate():
        missing = []
        if not Config.FB_APP_ID: missing.append("FB_APP_ID")
        if not Config.FB_APP_SECRET: missing.append("FB_APP_SECRET")
        if not Config.OPENROUTER_API_KEY: missing.append("OPENROUTER_API_KEY")
        if not Config.PEXELS_API_KEY: missing.append("PEXELS_API_KEY")
        
        if missing:
            raise ValueError(f"Missing environment variables: {', '.join(missing)}")
