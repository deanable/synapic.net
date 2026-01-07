import os
import google_auth_oauthlib.flow
import googleapiclient.discovery
import googleapiclient.errors
from youtube_transcript_api import YouTubeTranscriptApi
from google.oauth2.credentials import Credentials
from google.auth.transport.requests import Request
import config

class YouTubeClient:
    def __init__(self):
        self.youtube = None
        self.credentials = None

    def authenticate(self):
        """Authenticates the user and creates the YouTube API client."""
        # Disable OAuthlib's HTTPS verification when running locally.
        # *DO NOT* leave this option enabled in production.
        os.environ["OAUTHLIB_INSECURE_TRANSPORT"] = "1"

        api_service_name = "youtube"
        api_version = "v3"
        client_secrets_file = config.CLIENT_SECRETS_FILE
        scopes = config.SCOPES

        creds = None
        # The file token.json stores the user's access and refresh tokens, and is
        # created automatically when the authorization flow completes for the first
        # time.
        if os.path.exists("token.json"):
            creds = Credentials.from_authorized_user_file("token.json", scopes)
        
        # If there are no (valid) credentials available, let the user log in.
        if not creds or not creds.valid:
            if creds and creds.expired and creds.refresh_token:
                creds.refresh(Request())
            else:
                flow = google_auth_oauthlib.flow.InstalledAppFlow.from_client_secrets_file(
                    client_secrets_file, scopes)
                creds = flow.run_local_server(port=0)
            
            # Save the credentials for the next run
            with open("token.json", "w") as token:
                token.write(creds.to_json())

        self.youtube = googleapiclient.discovery.build(
            api_service_name, api_version, credentials=creds)
        print("Authenticated with YouTube successfully.")

    def get_video_details(self, video_id):
        """Fetches title, description, and tags for a video."""
        if not self.youtube:
            raise Exception("YouTube client not authenticated. Call authenticate() first.")

        request = self.youtube.videos().list(
            part="snippet,contentDetails,statistics",
            id=video_id
        )
        response = request.execute()

        if not response["items"]:
            return None

        snippet = response["items"][0]["snippet"]
        return {
            "title": snippet["title"],
            "description": snippet["description"],
            "tags": snippet.get("tags", []),
            "channelTitle": snippet["channelTitle"]
        }

    def get_transcript(self, video_id):
        """Fetches the transcript for a video."""
        try:
            transcript_list = YouTubeTranscriptApi.get_transcript(video_id)
            # Combine all text parts into one string
            full_transcript = " ".join([entry['text'] for entry in transcript_list])
            return full_transcript
        except Exception as e:
            print(f"Error fetching transcript: {e}")
            return None
