import json
import os
import datetime
from content_engine import ContentEngine

class JobManager:
    def __init__(self, queue_file="queue.json"):
        self.queue_file = queue_file
        self.content_engine = ContentEngine()
        self.data = self.load_data()

    def load_data(self):
        if os.path.exists(self.queue_file):
            with open(self.queue_file, 'r') as f:
                return json.load(f)
        return {"topics_queue": [], "posted_log": [], "target_page_id": "", "target_page_token": ""}

    def save_data(self):
        with open(self.queue_file, 'w') as f:
            json.dump(self.data, f, indent=2)

    def add_topic(self, topic):
        self.data["topics_queue"].append(topic)
        self.save_data()
        print(f"Added '{topic}' to queue.")

    def set_target_page(self, page_id, page_token):
        self.data["target_page_id"] = page_id
        self.data["target_page_token"] = page_token
        self.save_data()

    def run_job(self):
        print(f"\n[{datetime.datetime.now()}] Starting scheduled job...")
        
        # 1. Check Queue
        queue = self.data.get("topics_queue", [])
        if not queue:
            print("Queue is empty. Nothing to post.")
            return

        # 2. Get next topic
        topic = queue[0]
        print(f"Processing topic: {topic}")

        # 3. Generate Content
        # We catch errors here so one bad generation doesn't crash the loop
        try:
            article_text = self.content_engine.generate_article(topic)
            if not article_text:
                print("Failed to generate article text. Skipping.")
                return

            image_url = self.content_engine.get_image_url(topic)

            # 4. Post
            page_id = self.data.get("target_page_id")
            page_token = self.data.get("target_page_token")
            
            if not page_id or not page_token:
                print("No target page configured. Cannot post.")
                return

            post_id = self.content_engine.create_post(page_id, page_token, article_text, image_url)

            # 5. Success - Update Queue
            # Remove from queue
            self.data["topics_queue"].pop(0)
            
            # Add to log
            log_entry = {
                "topic": topic,
                "timestamp": datetime.datetime.now().isoformat(),
                "post_id": post_id
            }
            if "posted_log" not in self.data:
                self.data["posted_log"] = []
            self.data["posted_log"].append(log_entry)
            
            self.save_data()
            print("Job completed successfully.")

        except Exception as e:
            print(f"Job failed with error: {e}")
