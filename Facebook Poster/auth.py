import requests
from config import Config
import webbrowser
from flask import Flask, request
import threading
import time

class FacebookAuth:
    def __init__(self):
        self.app_id = Config.FB_APP_ID
        self.app_secret = Config.FB_APP_SECRET
        self.redirect_uri = Config.REDIRECT_URI
        self.base_url = "https://graph.facebook.com/v18.0"
        self.auth_code = None
        self.user_token = None

    def start_auth_flow(self):
        """Starts the OAuth flow by opening the browser and listening for the callback."""
        # 1. Start local server to listen for the callback
        app = Flask(__name__)
        
        # Suppress Flask CLI banner
        import logging
        log = logging.getLogger('werkzeug')
        log.setLevel(logging.ERROR)

        @app.route('/callback')
        def callback():
            code = request.args.get('code')
            if code:
                self.auth_code = code
                return "Authentication successful! You can close this window."
            return "Authentication failed."

        # Run server in a separate thread
        server_thread = threading.Thread(target=lambda: app.run(port=5000, ssl_context='adhoc'))
        server_thread.daemon = True
        server_thread.start()

        # 2. Open Browser
        # Scopes needed: pages_manage_posts, pages_read_engagement, public_profile
        scopes = "pages_manage_posts,pages_read_engagement,public_profile"
        auth_url = (
            f"https://www.facebook.com/v18.0/dialog/oauth?"
            f"client_id={self.app_id}&"
            f"redirect_uri={self.redirect_uri}&"
            f"scope={scopes}&"
            f"response_type=code"
        )
        
        print(f"Opening browser for authentication: {auth_url}")
        webbrowser.open(auth_url)

        # 3. Wait for code (simple polling for this script)
        print("Waiting for callback...")
        while self.auth_code is None:
            time.sleep(1)
        
        print("Authorization Code received.")
        return self.auth_code

    def exchange_code_for_token(self):
        """Exchanges the auth code for a short-lived user access token."""
        if not self.auth_code:
            raise ValueError("No auth code found. Run start_auth_flow first.")

        url = (
            f"{self.base_url}/oauth/access_token?"
            f"client_id={self.app_id}&"
            f"redirect_uri={self.redirect_uri}&"
            f"client_secret={self.app_secret}&"
            f"code={self.auth_code}"
        )
        response = requests.get(url)
        data = response.json()
        
        if 'access_token' in data:
            self.user_token = data['access_token']
            return self.user_token
        else:
            raise Exception(f"Failed to get token: {data}")

    def get_long_lived_token(self):
        """Exchanges the short-lived user token for a long-lived one (60 days)."""
        if not self.user_token:
            raise ValueError("No user token found.")

        url = (
            f"{self.base_url}/oauth/access_token?"
            f"grant_type=fb_exchange_token&"
            f"client_id={self.app_id}&"
            f"client_secret={self.app_secret}&"
            f"fb_exchange_token={self.user_token}"
        )
        response = requests.get(url)
        data = response.json()
        
        if 'access_token' in data:
            self.user_token = data['access_token']  # Update with long-lived token
            return self.user_token
        else:
            raise Exception(f"Failed to get long-lived token: {data}")

    def get_user_pages(self):
        """Fetches the list of pages the user manages."""
        if not self.user_token:
            raise ValueError("No user token found.")

        url = f"{self.base_url}/me/accounts?access_token={self.user_token}"
        response = requests.get(url)
        data = response.json()
        
        if 'data' in data:
            return data['data'] # Returns list of pages with access_token for each
        else:
            raise Exception(f"Failed to fetch pages: {data}")

if __name__ == "__main__":
    # Test flow
    auth = FacebookAuth()
    print("Starting Auth Flow...")
    auth.start_auth_flow()
    print("Exchanging for Token...")
    token = auth.exchange_code_for_token()
    print(f"Short Lived Token: {token[:10]}...")
    print("Exchanging for Long-Lived Token...")
    long_token = auth.get_long_lived_token()
    print(f"Long Lived Token: {long_token[:10]}...")
    print("Fetching Pages...")
    pages = auth.get_user_pages()
    for page in pages:
        print(f"Page: {page['name']} (ID: {page['id']})")
