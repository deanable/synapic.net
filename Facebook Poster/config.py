import os
from dotenv import load_dotenv

load_dotenv()

class Config:
    FB_APP_ID = os.getenv("FB_APP_ID")
    FB_APP_SECRET = os.getenv("FB_APP_SECRET")
    HF_TOKEN = os.getenv("HF_TOKEN")
    PEXELS_API_KEY = os.getenv("PEXELS_API_KEY")
    REDIRECT_URI = 'https://localhost:5000/callback' # Common for local testing
    
    @staticmethod
    def validate():
        missing = []
        if not Config.FB_APP_ID: missing.append("FB_APP_ID")
        if not Config.FB_APP_SECRET: missing.append("FB_APP_SECRET")
        if not Config.HF_TOKEN: missing.append("HF_TOKEN")
        if not Config.PEXELS_API_KEY: missing.append("PEXELS_API_KEY")
        
        if missing:
            raise ValueError(f"Missing environment variables: {', '.join(missing)}")
