import argparse
import sys
from youtube_client import YouTubeClient
from ai_optimizer import AIOptimizer
import os

def main():
    parser = argparse.ArgumentParser(description="AI YouTube Channel Optimiser")
    parser.add_argument("--video_id", help="The ID of the YouTube video to optimize", required=True)
    args = parser.parse_args()

    # check for client_secret.json
    if not os.path.exists("client_secret.json"):
        print("Error: client_secret.json not found. Please download it from Google Cloud Console and place it in the project root.")
        return

    print(f"--- Starting Optimization for Video ID: {args.video_id} ---")

    # 1. Initialize and Authenticate YouTube Client
    yt_client = YouTubeClient()
    try:
        yt_client.authenticate()
    except Exception as e:
        print(f"Authentication failed: {e}")
        return

    # 2. Fetch Video Details
    print("Fetching video details...")
    video_details = yt_client.get_video_details(args.video_id)
    if not video_details:
        print("Could not find video details.")
        return
    
    print(f"Found Video: {video_details['title']}")

    # 3. Fetch Transcript
    print("Fetching transcript...")
    transcript = yt_client.get_transcript(args.video_id)
    if not transcript:
        print("Could not fetch transcript. Does the video have captions enabled?")
        # Proceeding without transcript might be possible but less effective. 
        # For now, let's enforce it or give a warning.
        print("Warning: Optimization will be based on metadata only.")
        transcript = ""
    else:
        print(f"Transcript fetched desc ({len(transcript)} chars).")

    # 4. Run AI Optimization
    print("Running AI Optimization via OpenRouter...")
    ai_optimizer = AIOptimizer()
    optimization_result = ai_optimizer.generate_optimization(
        title=video_details['title'],
        description=video_details['description'],
        transcript=transcript
    )

    if not optimization_result:
        print("AI Optimization failed.")
        return

    # 5. Output Results
    print("\n" + "="*50)
    print("OPTIMIZATION RESULTS")
    print("="*50 + "\n")

    print("--- Optimized Titles ---")
    for i, title in enumerate(optimization_result.get('optimized_titles', []), 1):
        print(f"{i}. {title}")
    
    print("\n--- Optimized Description ---")
    print(optimization_result.get('optimized_description', 'N/A'))

    print("\n--- Suggested Chapters ---")
    for chapter in optimization_result.get('chapters', []):
        print(f"{chapter['timestamp']} - {chapter['title']}")

    print("\n" + "="*50)
    print("Done!")

if __name__ == "__main__":
    main()
